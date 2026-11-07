using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using FlashKit.Core.Abstractions;
using FlashKit.Core.Models;

namespace FlashKit.Services.Backup;

/// <summary>
/// 选择性分区备份。通过 Recovery 下的 ADB root 直接读取块设备，并回传电脑；
/// 不使用 fastboot fetch（国产机 fastboot 常常不暴露分区节点），也不使用 dd
/// （部分 TWRP 会把 dd 状态写到 stdout 污染文件）。
/// 未勾选分区不会创建文件，只会以名称形式记录在 manifest 的 notBackedUpPartitions 中。
/// </summary>
public sealed class BackupService : IBackupService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly IAdbClient _adb;
    private readonly ILoggingService _logging;

    public BackupService(IAdbClient adb, ILoggingService logging)
    {
        _adb = adb;
        _logging = logging;
    }

    public async Task<BackupResult> BackupAsync(
        BackupRunOptions options,
        IProgress<BackupProgress>? progress = null,
        Action<ProcessOutputLine>? onOutput = null,
        CancellationToken cancellationToken = default)
    {
        if (options.SelectedPartitions.Count == 0)
        {
            throw new BackupException("没有勾选任何分区。");
        }

        var id = await _adb.ShellAsync(options.Serial, "id", cancellationToken).ConfigureAwait(false);
        if (!id.Contains("uid=0", StringComparison.OrdinalIgnoreCase))
        {
            throw new BackupException(
                "ADB 不是 root，无法读取块设备。KSU 请先 adb root；TWRP 请确认 adb shell id 为 uid=0。");
        }

        var selected = options.SelectedPartitions
            .GroupBy(p => p.PartitionName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToArray();

        var super = selected.FirstOrDefault(p => p.IsSuperContainer);
        var logicalNames = options.AllPartitions
            .Where(p => p.Group == BackupPartitionGroup.SuperLogical && !p.IsSuperContainer)
            .Select(p => p.PartitionName)
            .ToArray();

        BackupPartitionSelection[] effective;
        string[] coveredBySuper;
        if (super is not null)
        {
            // super 已经包含同组逻辑分区，不再单独读取，避免重复与浪费空间；
            // 但同一批勾选的普通/高风险分区仍然要备份。
            effective = selected
                .Where(p => p.IsSuperContainer || p.Group != BackupPartitionGroup.SuperLogical)
                .ToArray();
            coveredBySuper = logicalNames;
        }
        else
        {
            effective = selected;
            coveredBySuper = Array.Empty<string>();
        }

        var warnings = new List<string>();
        var failed = new List<string>();
        var images = new List<BackupImageEntry>();
        var totalKnownBytes = effective.Sum(p => p.SizeBytes ?? 0);
        var unknownSizes = effective.Count(p => p.SizeBytes is null or <= 0);
        if (unknownSizes > 0)
        {
            warnings.Add($"{unknownSizes} 个选中分区大小未知，无法精确计算所需空间。");
        }

        if (super is null && selected.Any(p => p.Group == BackupPartitionGroup.SuperLogical))
        {
            warnings.Add("只勾选了 super 内逻辑分区而未勾选 super 整体；独立备份逻辑分区可能不完整，建议改选 super。");
        }

        var destinationRoot = Path.GetFullPath(options.DestinationRoot);
        Directory.CreateDirectory(destinationRoot);
        var outputDirectory = BuildOutputDirectory(destinationRoot, options.DeviceModel, options.Serial);
        EnsureSufficientSpace(destinationRoot, totalKnownBytes);
        var imagesDirectory = Path.Combine(outputDirectory, "images");
        Directory.CreateDirectory(imagesDirectory);

        progress?.Report(new BackupProgress
        {
            CurrentIndex = 0,
            TotalCount = effective.Length,
            TotalBytes = totalKnownBytes > 0 ? totalKnownBytes : null,
            Status = "准备备份"
        });

        var processedBytes = 0L;
        var cancelled = false;

        for (var index = 0; index < effective.Length; index++)
        {
            var item = effective[index];
            if (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }

            if (!Regex.IsMatch(item.PartitionName, "^[A-Za-z0-9._-]+$"))
            {
                failed.Add(item.PartitionName);
                warnings.Add($"{item.PartitionName} 含非法字符，已跳过。");
                continue;
            }

            var fileName = "images/" + SafeFileName(item.PartitionName) + ".img";
            var outputPath = Path.Combine(outputDirectory, fileName);
            var itemKnown = item.SizeBytes is > 0;
            var baseBytes = processedBytes;

            void ReportItem(double fraction, string status)
            {
                var overall = itemKnown && totalKnownBytes > 0
                    ? (baseBytes + (item.SizeBytes!.Value * Math.Clamp(fraction, 0, 1))) / totalKnownBytes * 100.0
                    : (index + Math.Clamp(fraction, 0, 1)) / effective.Length * 100.0;
                progress?.Report(new BackupProgress
                {
                    CurrentIndex = index + 1,
                    TotalCount = effective.Length,
                    PartitionName = item.PartitionName,
                    Percentage = Math.Clamp(overall, 0, 100),
                    BytesProcessed = baseBytes + (itemKnown ? (long)(item.SizeBytes!.Value * Math.Clamp(fraction, 0, 1)) : 0),
                    TotalBytes = totalKnownBytes > 0 ? totalKnownBytes : null,
                    Status = status
                });
            }

            ReportItem(0, $"正在读取 {item.PartitionName}");
            _logging.Info($"开始通过 ADB dd 读取 {item.PartitionName} → {outputPath}", "Backup");

            try
            {
                // 按规范使用 dd if=... bs=4M conv=noerror,sync。
                // toybox dd 会把最后一个块补齐到 4M，因此完成后必须按 blockdev 大小截断；
                // 部分 TWRP 还会把 records in/out 写到 stdout，截断同时清理这类尾部污染。
                var script = BuildDdReadScript(item);
                var transferTask = _adb.ExecOutToFileAsync(
                    options.Serial,
                    script,
                    outputPath,
                    onBytesWritten: null,
                    onOutput,
                    cancellationToken);

                // adb exec-out 直接把二进制写入文件，通过文件大小做低频进度估算。
                while (!transferTask.IsCompleted)
                {
                    await Task.Delay(400, cancellationToken).ConfigureAwait(false);
                    if (File.Exists(outputPath) && item.SizeBytes is > 0)
                    {
                        var currentLength = new FileInfo(outputPath).Length;
                        ReportItem((double)currentLength / item.SizeBytes.Value, $"正在读取 {item.PartitionName}");
                    }
                }

                var result = await transferTask.ConfigureAwait(false);
                if (result.Cancelled)
                {
                    cancelled = true;
                    TryDelete(outputPath);
                    break;
                }

                if (!result.IsSuccess)
                {
                    TryDelete(outputPath);
                    failed.Add(item.PartitionName);
                    var reason = result.ToReadableError();
                    warnings.Add($"{item.PartitionName} 读取失败：{reason}");
                    _logging.Error($"读取 {item.PartitionName} 失败：{reason}", "Backup");
                    ReportItem(1, $"{item.PartitionName} 读取失败");
                    continue;
                }

                var length = File.Exists(outputPath) ? new FileInfo(outputPath).Length : 0;
                if (length <= 0)
                {
                    TryDelete(outputPath);
                    failed.Add(item.PartitionName);
                    warnings.Add($"{item.PartitionName} 未生成有效文件。");
                    continue;
                }

                if (item.SizeBytes is > 0 && length != item.SizeBytes.Value)
                {
                    if (length > item.SizeBytes.Value)
                    {
                        // dd conv=sync 的块补齐 / TWRP stdout 状态都在尾部，按已知分区容量截断。
                        var rawLength = length;
                        await TruncateToAsync(outputPath, item.SizeBytes.Value, cancellationToken).ConfigureAwait(false);
                        length = new FileInfo(outputPath).Length;
                        _logging.Info(
                            $"{item.PartitionName} 原始 dd 输出 {rawLength} 字节，已按分区容量截断为 {item.SizeBytes.Value} 字节。",
                            "Backup");
                    }
                }

                if (item.SizeBytes is > 0 && length != item.SizeBytes.Value)
                {
                    TryDelete(outputPath);
                    failed.Add(item.PartitionName);
                    warnings.Add(
                        $"{item.PartitionName} 读取长度不完整：{PartitionInfo.FormatBytes(length)} / " +
                        $"{PartitionInfo.FormatBytes(item.SizeBytes.Value)}。");
                    continue;
                }

                images.Add(new BackupImageEntry
                {
                    PartitionName = item.PartitionName,
                    FileName = fileName,
                    SizeBytes = length,
                    Risk = item.Risk,
                    SourceBlockPath = item.SourceBlockPath
                });
                processedBytes += length;
                ReportItem(1, $"{item.PartitionName} 备份完成");
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                TryDelete(outputPath);
                break;
            }
            catch (Exception ex)
            {
                TryDelete(outputPath);
                failed.Add(item.PartitionName);
                warnings.Add($"{item.PartitionName} 备份异常：{ex.Message}");
                _logging.Error($"备份 {item.PartitionName} 异常：{ex.Message}", "Backup");
            }
        }

        var succeededNames = images.Select(i => i.PartitionName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var finalCoveredBySuper = super is not null && succeededNames.Contains(super.PartitionName)
            ? coveredBySuper
            : Array.Empty<string>();
        var included = succeededNames
            .Concat(finalCoveredBySuper)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var notBackedUp = options.AllPartitions
            .Select(p => p.PartitionName)
            .Where(n => !included.Contains(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (effective.Any(p => p.SizeBytes is not > 0))
        {
            warnings.Add("存在容量未知的分区；dd conv=sync 会补齐块，无法按容量截断，请在 TWRP/KSU 下重新读取分区容量。");
        }

        var manifest = new BackupManifest
        {
            ToolVersion = AppInfo.Version,
            Serial = options.Serial,
            DeviceCodename = options.DeviceCodename,
            DeviceModel = options.DeviceModel,
            AndroidRelease = options.AndroidRelease,
            TwrpVersion = options.TwrpVersion,
            Mode = options.Transport == BackupTransport.Twrp ? "twrp" : "boot-root",
            IsSuperWholeBackup = super is not null,
            DdUsedNoErrorSync = true,
            CreatedAt = DateTimeOffset.Now,
            OutputDirectory = outputDirectory,
            IsSelective = true,
            Images = images,
            NotBackedUpPartitions = notBackedUp,
            CoveredBySuperPartitions = finalCoveredBySuper.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray(),
            FailedPartitions = failed.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            Warnings = warnings,
            TotalBytes = images.Sum(i => i.SizeBytes),
            Cancelled = cancelled
        };

        var manifestPath = Path.Combine(outputDirectory, "manifest.json");
        await File.WriteAllTextAsync(
            manifestPath,
            JsonSerializer.Serialize(manifest, JsonOptions),
            cancellationToken.IsCancellationRequested ? CancellationToken.None : cancellationToken).ConfigureAwait(false);

        _logging.Info(
            $"备份结束：成功 {manifest.Images.Count}，失败 {manifest.FailedPartitions.Count}，未包含 {manifest.NotBackedUpPartitions.Count}",
            "Backup");

        return new BackupResult
        {
            OutputDirectory = outputDirectory,
            Manifest = manifest,
            ManifestPath = manifestPath
        };
    }

    private static string BuildOutputDirectory(string root, string? model, string serial)
    {
        var safeModel = SafeFileName(string.IsNullOrWhiteSpace(model) ? "device" : model);
        var safeSerial = SafeFileName(serial);
        var baseName = $"{safeModel}_{safeSerial}_{DateTime.Now:yyyyMMdd_HHmmss}";
        var candidate = Path.Combine(root, baseName);
        var index = 1;
        while (Directory.Exists(candidate))
        {
            candidate = Path.Combine(root, $"{baseName}_{index++}");
        }

        return candidate;
    }

    private static string BuildDdReadScript(BackupPartitionSelection item)
    {
        var escapedPath = item.SourceBlockPath?.Replace("\"", "\\\"", StringComparison.Ordinal);
        if (!string.IsNullOrWhiteSpace(escapedPath))
        {
            return $"p=\"{escapedPath}\"; [ -e \"$p\" ] || exit 1; " +
                   "exec dd if=\"$p\" bs=4M conv=noerror,sync 2>/dev/null";
        }

        var name = item.PartitionName;
        return $"for p in /dev/block/by-name/{name} /dev/block/bootdevice/by-name/{name} " +
               $"/dev/block/mapper/{name}; do " +
               "[ -e \"$p\" ] && exec dd if=\"$p\" bs=4M conv=noerror,sync 2>/dev/null; done; exit 1";
    }

    private static async Task TruncateToAsync(string path, long length, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Write,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous);
        stream.SetLength(length);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void EnsureSufficientSpace(string root, long requiredBytes)
    {
        if (requiredBytes <= 0)
        {
            return;
        }

        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root))!);
            const long reserve = 512L * 1024 * 1024;
            if (drive.AvailableFreeSpace < requiredBytes + reserve)
            {
                throw new BackupException(
                    $"备份空间不足：预计需要 {PartitionInfo.FormatBytes(requiredBytes)}，" +
                    $"可用 {PartitionInfo.FormatBytes(drive.AvailableFreeSpace)}。");
            }
        }
        catch (BackupException)
        {
            throw;
        }
        catch
        {
            // 无法读取磁盘信息时交给实际写入报错。
        }
    }

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        if (string.IsNullOrWhiteSpace(safe) || safe is "." or "..")
        {
            return "partition";
        }

        return safe;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 清理失败不覆盖原始错误。
        }
    }
}
