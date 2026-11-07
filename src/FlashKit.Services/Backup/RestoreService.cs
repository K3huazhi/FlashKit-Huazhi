using System.Text.Json;
using FlashKit.Core.Abstractions;
using FlashKit.Core.Models;

namespace FlashKit.Services.Backup;

/// <summary>
/// 从本机备份 manifest 还原。系统/KSU 与 TWRP 用 adb push + dd 写回；
/// Fastboot 用 fastboot flash。默认遇到失败停止，调用方可选择继续。
/// </summary>
public sealed class RestoreService : IRestoreService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IAdbClient _adb;
    private readonly IFastbootClient _fastboot;
    private readonly ILoggingService _logging;

    public RestoreService(IAdbClient adb, IFastbootClient fastboot, ILoggingService logging)
    {
        _adb = adb;
        _fastboot = fastboot;
        _logging = logging;
    }

    public async Task<BackupManifest> ReadManifestAsync(string manifestPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(manifestPath))
        {
            throw new BackupException("manifest 不存在：" + manifestPath);
        }

        var json = await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        var manifest = JsonSerializer.Deserialize<BackupManifest>(json, JsonOptions)
                       ?? throw new BackupException("manifest 解析失败。");
        if (manifest.Images.Count == 0)
        {
            throw new BackupException("manifest 中没有可还原的镜像。");
        }

        return manifest;
    }

    public async Task<RestoreResult> RestoreAsync(
        RestoreRequest request,
        IProgress<RestoreProgress>? progress = null,
        Action<ProcessOutputLine>? onOutput = null,
        CancellationToken cancellationToken = default)
    {
        var manifest = await ReadManifestAsync(request.ManifestPath, cancellationToken).ConfigureAwait(false);
        // 以 manifest 所在目录为准，允许用户移动整个备份目录；manifest 内的 outputDirectory 只作记录。
        var backupDirectory = Path.GetDirectoryName(Path.GetFullPath(request.ManifestPath))
                              ?? throw new BackupException("无法确定 manifest 所在目录。");
        if (!Directory.Exists(backupDirectory))
        {
            throw new BackupException("备份目录不存在：" + backupDirectory);
        }

        var selected = request.SelectedPartitions.Count > 0
            ? manifest.Images.Where(i => request.SelectedPartitions.Contains(i.PartitionName, StringComparer.OrdinalIgnoreCase)).ToArray()
            : manifest.Images.ToArray();
        if (selected.Length == 0)
        {
            throw new BackupException("没有选择可还原的分区。");
        }

        var warnings = new List<string>();
        await ValidateDeviceAsync(request, manifest, warnings, cancellationToken).ConfigureAwait(false);

        var useFastboot = request.DeviceMode == DeviceMode.Fastboot;
        var adbRoot = false;
        if (!useFastboot)
        {
            adbRoot = await EnsureRootAsync(request.Serial, cancellationToken).ConfigureAwait(false);
            if (!adbRoot)
            {
                throw new BackupException("还原需要 ADB root。请确认 KSU/TWRP 的 adb shell id 为 uid=0。");
            }
        }

        foreach (var item in selected.Where(i => PartitionRules.GetRisk(i.PartitionName) != PartitionRisk.Normal))
        {
            warnings.Add($"高风险分区：{item.PartitionName}，已纳入本次还原，请确认已备份。");
        }

        var results = new List<RestoreItemResult>();
        var cancelled = false;
        var remoteDirectory = $"/data/local/tmp/flashkit-restore-{Guid.NewGuid():N}";
        var remoteCreated = false;

        for (var index = 0; index < selected.Length; index++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }

            var item = selected[index];
            var percentBase = index * 100.0 / selected.Length;
            progress?.Report(new RestoreProgress
            {
                CurrentIndex = index + 1,
                TotalCount = selected.Length,
                PartitionName = item.PartitionName,
                Percentage = percentBase,
                Status = $"正在还原 {item.PartitionName}"
            });

            var imagePath = ResolveImagePath(backupDirectory, item);
            if (!File.Exists(imagePath))
            {
                var failure = Failure(item.PartitionName, $"镜像不存在：{imagePath}");
                results.Add(failure);
                if (!request.ContinueOnError)
                {
                    break;
                }

                continue;
            }

            var fileLength = new FileInfo(imagePath).Length;
            if (item.SizeBytes > 0 && fileLength != item.SizeBytes)
            {
                var failure = Failure(item.PartitionName,
                    $"镜像大小与 manifest 不一致：{fileLength} / {item.SizeBytes}，已拒绝写入。");
                results.Add(failure);
                warnings.Add(failure.Message);
                if (!request.ContinueOnError)
                {
                    break;
                }

                continue;
            }

            _logging.Info(
                $"还原目标 serial={request.Serial} partition={item.PartitionName} " +
                $"image={imagePath} source=本机备份",
                "Restore");

            try
            {
                ProcessResult result;
                if (useFastboot)
                {
                    if (item.PartitionName.StartsWith("super", StringComparison.OrdinalIgnoreCase) ||
                        PartitionRules.IsDynamic(item.PartitionName))
                    {
                        warnings.Add($"{item.PartitionName} 是动态分区；如果 fastboot 报分区不存在，请先 fastboot reboot fastboot。");
                    }

                    result = await _fastboot.FlashAsync(
                        request.Serial,
                        item.PartitionName,
                        imagePath,
                        onOutput,
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    if (!remoteCreated)
                    {
                        var mk = await _adb.ExecuteRawAsync(
                            request.Serial,
                            ["shell", "mkdir", "-p", remoteDirectory],
                            onOutput,
                            cancellationToken,
                            logToHistory: false,
                            timeout: TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                        if (mk.ExitCode != 0)
                        {
                            throw new BackupException("创建临时目录失败：" + mk.ToReadableError());
                        }

                        remoteCreated = true;
                    }

                    var remotePath = $"{remoteDirectory}/{item.PartitionName}.img";
                    var push = await _adb.PushAsync(
                        request.Serial,
                        imagePath,
                        remotePath,
                        onOutput,
                        cancellationToken).ConfigureAwait(false);
                    if (push.ExitCode != 0)
                    {
                        throw new BackupException("adb push 失败：" + push.ToReadableError());
                    }

                    var script =
                        $"p=/dev/block/by-name/{item.PartitionName}; " +
                        $"[ -e \"$p\" ] || p=/dev/block/bootdevice/by-name/{item.PartitionName}; " +
                        $"[ -e \"$p\" ] || p=/dev/block/mapper/{item.PartitionName}; " +
                        $"[ -e \"$p\" ] || {{ echo \"partition not found\"; exit 2; }}; " +
                        $"dd if=\"{remotePath}\" of=\"$p\" bs=4M; " +
                        $"rc=$?; rm -f \"{remotePath}\"; exit $rc";
                    result = await _adb.ExecuteRawAsync(
                        request.Serial,
                        ["shell", script],
                        onOutput,
                        cancellationToken,
                        logToHistory: true,
                        timeout: TimeSpan.FromMinutes(30)).ConfigureAwait(false);
                }

                if (result.ExitCode != 0)
                {
                    results.Add(Failure(item.PartitionName,
                        $"退出码 {result.ExitCode}：{result.ToReadableError()}"));
                    _logging.Error($"还原 {item.PartitionName} 失败：{result.ToReadableError()}", "Restore");
                    if (!request.ContinueOnError)
                    {
                        break;
                    }
                }
                else
                {
                    results.Add(new RestoreItemResult
                    {
                        PartitionName = item.PartitionName,
                        Success = true,
                        ExitCode = 0,
                        Message = "写入完成"
                    });
                    _logging.Info($"还原 {item.PartitionName} 完成", "Restore");
                }
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                break;
            }
            catch (Exception ex)
            {
                results.Add(Failure(item.PartitionName, ex.Message));
                _logging.Error($"还原 {item.PartitionName} 异常：{ex.Message}", "Restore");
                if (!request.ContinueOnError)
                {
                    break;
                }
            }

            progress?.Report(new RestoreProgress
            {
                CurrentIndex = index + 1,
                TotalCount = selected.Length,
                PartitionName = item.PartitionName,
                Percentage = (index + 1) * 100.0 / selected.Length,
                Status = results.LastOrDefault()?.Success == true
                    ? $"{item.PartitionName} 完成"
                    : $"{item.PartitionName} 失败"
            });
        }

        if (remoteCreated)
        {
            try
            {
                await _adb.ExecuteRawAsync(
                    request.Serial,
                    ["shell", "rm", "-rf", remoteDirectory],
                    cancellationToken: CancellationToken.None,
                    logToHistory: false,
                    timeout: TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        return new RestoreResult
        {
            ManifestPath = request.ManifestPath,
            BackupDirectory = backupDirectory,
            Results = results,
            Warnings = warnings,
            Cancelled = cancelled
        };
    }

    private async Task ValidateDeviceAsync(
        RestoreRequest request,
        BackupManifest manifest,
        List<string> warnings,
        CancellationToken ct)
    {
        if (!string.Equals(manifest.Serial, request.Serial, StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add($"manifest 来源序列号 {manifest.Serial} 与当前设备 {request.Serial} 不一致。");
        }

        if (string.IsNullOrWhiteSpace(manifest.DeviceModel) && string.IsNullOrWhiteSpace(manifest.DeviceCodename))
        {
            warnings.Add("manifest 未记录设备型号，无法校验来源设备。");
            return;
        }

        string model;
        string codename;
        if (request.DeviceMode == DeviceMode.Fastboot)
        {
            model = (await _fastboot.GetVarAsync(request.Serial, "product", ct).ConfigureAwait(false) ?? string.Empty).Trim();
            codename = model;
            warnings.Add("Fastboot 模式无法读取 ro.product.model，型号校验以 fastboot product 为准。");
        }
        else
        {
            model = (await _adb.GetPropAsync(request.Serial, "ro.product.model", ct).ConfigureAwait(false) ?? string.Empty).Trim();
            codename = (await _adb.GetPropAsync(request.Serial, "ro.product.device", ct).ConfigureAwait(false) ?? string.Empty).Trim();
        }
        var modelMismatch = !string.IsNullOrWhiteSpace(manifest.DeviceModel) &&
                            !string.IsNullOrWhiteSpace(model) &&
                            !manifest.DeviceModel.Equals(model, StringComparison.OrdinalIgnoreCase);
        var codenameMismatch = !string.IsNullOrWhiteSpace(manifest.DeviceCodename) &&
                               !string.IsNullOrWhiteSpace(codename) &&
                               !manifest.DeviceCodename.Equals(codename, StringComparison.OrdinalIgnoreCase);
        if ((modelMismatch || codenameMismatch) && !request.AllowModelMismatch)
        {
            throw new BackupException(
                $"manifest 设备不匹配：备份 {manifest.DeviceModel}/{manifest.DeviceCodename}，" +
                $"当前 {model}/{codename}。如确认继续，请显式允许跨型号还原并记录日志。");
        }

        if (modelMismatch || codenameMismatch)
        {
            warnings.Add($"已显式允许跨型号还原：{manifest.DeviceModel}/{manifest.DeviceCodename} → {model}/{codename}");
        }
    }

    private async Task<bool> EnsureRootAsync(string serial, CancellationToken ct)
    {
        try
        {
            var id = await _adb.ShellAsync(serial, "id", ct, logToHistory: false).ConfigureAwait(false);
            return id.Contains("uid=0", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string ResolveImagePath(string backupDirectory, BackupImageEntry entry)
    {
        var combined = Path.GetFullPath(Path.Combine(backupDirectory, entry.FileName));
        var root = Path.GetFullPath(backupDirectory) + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new BackupException("manifest 镜像路径越界，已拒绝：" + entry.FileName);
        }

        return combined;
    }

    private static RestoreItemResult Failure(string partition, string message) => new()
    {
        PartitionName = partition,
        Success = false,
        ExitCode = -1,
        Message = message
    };
}
