using System.Globalization;
using FlashKit.Core.Abstractions;
using FlashKit.Core.Models;

namespace FlashKit.Services.Backup;

/// <summary>
/// 备份环境探测：开机 Root/KSU 或 TWRP。官方 Recovery、sideload、fastboot 明确拒绝。
/// 分区枚举只读 /dev/block 与 blockdev，不写设备。
/// </summary>
public sealed class BackupEnvironmentService : IBackupEnvironmentService
{
    private readonly IAdbClient _adb;
    private readonly ILoggingService _logging;
    private readonly Dictionary<string, BackupEnvironmentInfo> _cache = new(StringComparer.OrdinalIgnoreCase);

    public BackupEnvironmentService(IAdbClient adb, ILoggingService logging)
    {
        _adb = adb;
        _logging = logging;
    }

    public async Task<BackupEnvironmentInfo> DetectAsync(
        AndroidDevice device,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        var key = $"{device.Serial}:{device.Mode}";
        if (!forceRefresh && _cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var info = await DetectCoreAsync(device, cancellationToken).ConfigureAwait(false);
        _cache[key] = info;
        return info;
    }

    public void Invalidate(string serial)
    {
        foreach (var key in _cache.Keys.Where(k => k.StartsWith(serial + ":", StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            _cache.Remove(key);
        }
    }

    private async Task<BackupEnvironmentInfo> DetectCoreAsync(AndroidDevice device, CancellationToken ct)
    {
        if (device.State == DeviceConnectionState.Unauthorized)
        {
            return Unsupported(BackupTransport.BootRoot, "设备未授权。请在手机上允许 USB 调试后重试。");
        }

        if (device.Mode == DeviceMode.Sideload)
        {
            return Unsupported(BackupTransport.BootRoot, "设备处于 sideload。sideload 不能读取块设备分区。");
        }

        if (device.Mode == DeviceMode.Fastboot)
        {
            return Unsupported(BackupTransport.BootRoot, "Fastboot 模式不读取分区；请进入系统（KSU）或 TWRP。");
        }

        if (device.Mode is not (DeviceMode.System or DeviceMode.Recovery))
        {
            return Unsupported(BackupTransport.BootRoot, "当前设备模式不支持分区备份。");
        }

        string androidRelease;
        string codename;
        string model;
        try
        {
            androidRelease = (await _adb.GetPropAsync(device.Serial, "ro.build.version.release", ct).ConfigureAwait(false) ?? string.Empty).Trim();
            codename = (await _adb.GetPropAsync(device.Serial, "ro.product.device", ct).ConfigureAwait(false) ?? string.Empty).Trim();
            model = (await _adb.GetPropAsync(device.Serial, "ro.product.model", ct).ConfigureAwait(false) ?? string.Empty).Trim();
        }
        catch (Exception ex)
        {
            return Unsupported(BackupTransport.BootRoot, $"读取设备属性失败：{ex.Message}");
        }

        if (device.Mode == DeviceMode.Recovery)
        {
            var twrp = (await _adb.GetPropAsync(device.Serial, "ro.twrp.version", ct).ConfigureAwait(false) ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(twrp))
            {
                return Unsupported(
                    BackupTransport.Twrp,
                    "未检测到 TWRP 特征，官方 Recovery 无法直接读取块设备分区。请刷入 TWRP 或改用开机 KSU。",
                    androidRelease,
                    codename,
                    model);
            }

            var root = await EnsureRecoveryRootAsync(device.Serial, ct).ConfigureAwait(false);
            if (!root)
            {
                return Unsupported(
                    BackupTransport.Twrp,
                    "TWRP ADB 未取得 root，无法读取块设备。请检查 TWRP 版本或调试设置。",
                    androidRelease,
                    codename,
                    model,
                    twrp);
            }

            var catalog = await ReadPartitionsAsync(device.Serial, includeMapper: true, ct).ConfigureAwait(false);
            return new BackupEnvironmentInfo
            {
                Transport = BackupTransport.Twrp,
                IsSupported = catalog.Partitions.Count > 0,
                IsRoot = true,
                TwrpVersion = twrp,
                AndroidRelease = androidRelease,
                Codename = codename,
                Model = model,
                Catalog = catalog,
                Message = catalog.Partitions.Count > 0
                    ? $"TWRP {twrp} · Android {androidRelease} · {catalog.Partitions.Count} 个分区"
                    : "TWRP 已确认，但没有读取到 by-name/mapper 分区。"
            };
        }

        var rootOk = await EnsureBootRootAsync(device.Serial, ct).ConfigureAwait(false);
        if (!rootOk)
        {
            return Unsupported(
                BackupTransport.BootRoot,
                "未取得可用 Root。请确认 KernelSU 已授予 ADB root，或改用 TWRP 模式。",
                androidRelease,
                codename,
                model);
        }

        var systemCatalog = await ReadPartitionsAsync(device.Serial, includeMapper: false, ct).ConfigureAwait(false);
        return new BackupEnvironmentInfo
        {
            Transport = BackupTransport.BootRoot,
            IsSupported = systemCatalog.Partitions.Count > 0,
            IsRoot = true,
            AndroidRelease = androidRelease,
            Codename = codename,
            Model = model,
            Catalog = systemCatalog,
            Message = systemCatalog.Partitions.Count > 0
                ? $"KSU / Boot Root · Android {androidRelease} · {systemCatalog.Partitions.Count} 个分区"
                : "Root 可用，但没有读取到 by-name 分区。"
        };
    }

    private async Task<bool> EnsureBootRootAsync(string serial, CancellationToken ct)
    {
        if (await IsRootAsync(serial, ct).ConfigureAwait(false))
        {
            return true;
        }

        try
        {
            var result = await _adb.ExecuteRawAsync(
                serial,
                ["root"],
                cancellationToken: ct,
                logToHistory: true,
                timeout: TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            if (result.ExitCode == 0)
            {
                await Task.Delay(1200, ct).ConfigureAwait(false);
                if (await IsRootAsync(serial, ct).ConfigureAwait(false))
                {
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            _logging.Warn($"adb root 失败：{ex.Message}", "Backup");
        }

        try
        {
            var output = await _adb.ShellAsync(serial, "su -c id", ct, logToHistory: false).ConfigureAwait(false);
            return output.Contains("uid=0", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logging.Warn($"su -c id 失败：{ex.Message}", "Backup");
            return false;
        }
    }

    private async Task<bool> EnsureRecoveryRootAsync(string serial, CancellationToken ct)
    {
        if (await IsRootAsync(serial, ct).ConfigureAwait(false))
        {
            return true;
        }

        try
        {
            var result = await _adb.ExecuteRawAsync(
                serial,
                ["root"],
                cancellationToken: ct,
                logToHistory: true,
                timeout: TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            if (result.ExitCode == 0)
            {
                await Task.Delay(1200, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logging.Warn($"Recovery adb root 失败：{ex.Message}", "Backup");
        }

        return await IsRootAsync(serial, ct).ConfigureAwait(false);
    }

    private async Task<bool> IsRootAsync(string serial, CancellationToken ct)
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

    private async Task<PartitionCatalog> ReadPartitionsAsync(string serial, bool includeMapper, CancellationToken ct)
    {
        var script = BuildScript(includeMapper);
        var result = await _adb.ExecuteRawAsync(
            serial,
            ["shell", script],
            cancellationToken: ct,
            logToHistory: false,
            timeout: TimeSpan.FromSeconds(60)).ConfigureAwait(false);

        var entries = new Dictionary<string, PartitionInfo>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        if (result.ExitCode != 0 && string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            warnings.Add("分区枚举命令失败：" + result.ToReadableError());
        }

        foreach (var line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('|');
            if (parts.Length < 3)
            {
                continue;
            }

            var name = parts[0].Trim();
            var path = parts[1].Trim();
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path) || name is "by-uuid" or "by-path")
            {
                continue;
            }

            long? size = long.TryParse(parts[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
                ? parsed
                : null;
            if (!entries.TryGetValue(name, out var existing) || existing.SizeBytes is null && size is not null)
            {
                entries[name] = new PartitionInfo
                {
                    Name = name,
                    SizeBytes = size,
                    Source = PartitionSource.AdbBlockByyName,
                    Risk = PartitionRules.GetRisk(name),
                    IsDynamic = PartitionRules.IsDynamic(name),
                    BlockPath = path
                };
            }
        }

        if (entries.Values.Any(p => p.SizeBytes is null))
        {
            warnings.Add("部分分区块大小未知；备份后无法按容量校验长度。");
        }

        var catalog = new PartitionCatalog
        {
            Partitions = entries.Values
                .OrderBy(p => PartitionRules.GetRecommendedOrder(p.Name))
                .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            Source = PartitionSource.AdbBlockByyName,
            HasSizes = entries.Values.All(p => p.SizeBytes is not null),
            Warnings = warnings,
            DeviceCodename = (await _adb.GetPropAsync(serial, "ro.product.device", ct).ConfigureAwait(false) ?? string.Empty).Trim(),
            CurrentSlot = (await _adb.GetPropAsync(serial, "ro.boot.slot_suffix", ct).ConfigureAwait(false) ?? string.Empty).Trim(),
            IsUserspace = null
        };
        return catalog;
    }

    private static string BuildScript(bool includeMapper) =>
        "for base in /dev/block/by-name /dev/block/bootdevice/by-name /dev/block/platform/*/by-name; do " +
        "[ -d \"$base\" ] || continue; " +
        "for p in \"$base\"/*; do [ -e \"$p\" ] || continue; n=\"${p##*/}\"; " +
        "s=$(blockdev --getsize64 \"$p\" 2>/dev/null); " +
        "echo \"$n|$p|$s\"; done; done; " +
        (includeMapper
            ? "for p in /dev/block/mapper/*; do [ -e \"$p\" ] || continue; n=\"${p##*/}\"; " +
              "[ \"$n\" = \"by-uuid\" ] && continue; " +
              "s=$(blockdev --getsize64 \"$p\" 2>/dev/null); echo \"$n|$p|$s\"; done; "
            : string.Empty) +
        "true";

    private static BackupEnvironmentInfo Unsupported(
        BackupTransport transport,
        string message,
        string? androidRelease = null,
        string? codename = null,
        string? model = null,
        string? twrp = null) => new()
    {
        Transport = transport,
        IsSupported = false,
        IsRoot = false,
        TwrpVersion = twrp,
        AndroidRelease = androidRelease,
        Codename = codename,
        Model = model,
        Message = message
    };
}
