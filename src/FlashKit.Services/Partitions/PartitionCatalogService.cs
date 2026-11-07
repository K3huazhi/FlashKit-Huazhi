using System.Globalization;
using FlashKit.Core.Abstractions;
using FlashKit.Core.Models;
using FlashKit.Core.Parsing;

namespace FlashKit.Services.Partitions;

/// <summary>
/// 分区目录的唯一来源：
/// - 系统模式：adb shell ls /dev/block/by-name（失败时尝试 platform by-name / mapper）；
/// - Fastboot：fastboot getvar all 中的 partition-size:*；
/// 不维护任何写死的分区列表。会话内缓存 30 秒，模式切换后可手动刷新。
/// </summary>
public sealed class PartitionCatalogService : IPartitionCatalogService
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);
    private readonly IAdbClient _adb;
    private readonly IFastbootClient _fastboot;
    private readonly ILoggingService _logging;
    private readonly Dictionary<string, (PartitionCatalog Catalog, DateTimeOffset At)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public PartitionCatalogService(IAdbClient adb, IFastbootClient fastboot, ILoggingService logging)
    {
        _adb = adb;
        _fastboot = fastboot;
        _logging = logging;
    }

    public async Task<PartitionCatalog> GetAsync(
        string serial,
        DeviceMode mode,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        var key = $"{serial}|{mode}";
        if (!forceRefresh)
        {
            lock (_gate)
            {
                if (_cache.TryGetValue(key, out var cached) &&
                    DateTimeOffset.Now - cached.At < CacheDuration)
                {
                    return cached.Catalog;
                }
            }
        }

        var catalog = mode switch
        {
            DeviceMode.System => await ReadSystemPartitionsAsync(serial, cancellationToken).ConfigureAwait(false),
            DeviceMode.Recovery => await ReadRecoveryPartitionsAsync(serial, cancellationToken).ConfigureAwait(false),
            DeviceMode.Fastboot => await ReadFastbootPartitionsAsync(serial, cancellationToken).ConfigureAwait(false),
            _ => new PartitionCatalog
            {
                Partitions = Array.Empty<PartitionInfo>(),
                Source = PartitionSource.AdbBlockByyName,
                Warnings = ["当前模式不支持读取分区目录。请在系统或 Fastboot 模式下重试。"]
            }
        };

        lock (_gate)
        {
            _cache[key] = (catalog, DateTimeOffset.Now);
        }

        return catalog;
    }

    public void Invalidate(string serial)
    {
        lock (_gate)
        {
            foreach (var key in _cache.Keys.Where(k => k.StartsWith(serial + "|", StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                _cache.Remove(key);
            }
        }
    }

    private async Task<PartitionCatalog> ReadSystemPartitionsAsync(string serial, CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        var names = new List<string>();

        var byName = await RunShellAsync(serial, "ls /dev/block/by-name", cancellationToken).ConfigureAwait(false);
        names.AddRange(DeviceOutputParser.ParsePartitionNamesFromLs(byName));

        if (names.Count == 0)
        {
            var platform = await RunShellAsync(serial, "ls /dev/block/platform/*/by-name", cancellationToken).ConfigureAwait(false);
            names.AddRange(DeviceOutputParser.ParsePartitionNamesFromLs(platform));
            if (names.Count > 0)
            {
                warnings.Add("无法读取 /dev/block/by-name，已改用平台 by-name 路径");
            }
        }

        if (names.Count == 0)
        {
            var mapper = await RunShellAsync(serial, "ls /dev/block/mapper", cancellationToken).ConfigureAwait(false);
            names.AddRange(DeviceOutputParser.ParsePartitionNamesFromLs(mapper));
            if (names.Count > 0)
            {
                warnings.Add("by-name 目录不可读，当前列表来自 /dev/block/mapper，只包含动态分区");
            }
        }

        if (names.Count == 0)
        {
            warnings.Add("无法枚举分区。可能是系统权限限制，请确认 ADB 已授权；刷入仍可在 Fastboot 模式读取真实分区表。");
            _logging.Warn($"系统模式分区枚举失败：{serial}", "Partition");
        }

        var slotTask = _adb.GetPropAsync(serial, "ro.boot.slot_suffix", cancellationToken);
        var codenameTask = _adb.GetPropAsync(serial, "ro.product.device", cancellationToken);
        await Task.WhenAll(slotTask, codenameTask).ConfigureAwait(false);

        var partitions = names
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(name => new PartitionInfo
            {
                Name = name,
                SizeBytes = null,
                Source = PartitionSource.AdbBlockByyName,
                Risk = PartitionRules.GetRisk(name),
                IsDynamic = PartitionRules.IsDynamic(name)
            })
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new PartitionCatalog
        {
            Partitions = partitions,
            Source = PartitionSource.AdbBlockByyName,
            HasSizes = false,
            Warnings = warnings,
            CurrentSlot = string.IsNullOrWhiteSpace(slotTask.Result) ? null : slotTask.Result,
            DeviceCodename = string.IsNullOrWhiteSpace(codenameTask.Result) ? null : codenameTask.Result
        };
    }

    /// <summary>
    /// Recovery / ADB root 备份路径。国产机 fastboot 经常不暴露 by-name，但 recovery 的
    /// adbd 通常是 root，可直接从 /dev/block/by-name、platform by-name 或 mapper 读取。
    /// 这里一次性用 shell 脚本枚举路径和大小，避免几十次 adb shell 往返。
    /// </summary>
    private async Task<PartitionCatalog> ReadRecoveryPartitionsAsync(string serial, CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        var id = await RunShellAsync(serial, "id", cancellationToken).ConfigureAwait(false);
        var isRoot = id.Contains("uid=0", StringComparison.OrdinalIgnoreCase);
        if (!isRoot)
        {
            warnings.Add("Recovery ADB 当前不是 root；请使用 TWRP 或可 root 的 recovery。分区可能只能列出但不能读取。");
        }

        const string script = """
            for d in /dev/block/by-name/* /dev/block/bootdevice/by-name/* /dev/block/platform/*/by-name/* /dev/block/mapper/*; do
              [ -e "$d" ] || continue
              n=${d##*/}
              s=$(blockdev --getsize64 "$d" 2>/dev/null)
              [ -n "$s" ] || s=$(( $(cat /sys/class/block/$n/size 2>/dev/null) * 512 ))
              echo "$n|$d|$s"
            done
            """;
        var listing = await RunShellAsync(serial, script, cancellationToken).ConfigureAwait(false);
        var entries = new Dictionary<string, (string Path, long? Size)>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in listing.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Trim().Split('|');
            if (parts.Length < 2)
            {
                continue;
            }

            var name = parts[0].Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            long? size = parts.Length >= 3 &&
                         long.TryParse(parts[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) &&
                         parsed > 0
                ? parsed
                : null;

            // by-name 在前，mapper 在后；同名时保留先出现的真实 by-name 节点。
            entries.TryAdd(name, (parts[1].Trim(), size));
        }

        if (entries.Count == 0)
        {
            var byName = await RunShellAsync(serial, "ls /dev/block/by-name", cancellationToken).ConfigureAwait(false);
            foreach (var name in DeviceOutputParser.ParsePartitionNamesFromLs(byName))
            {
                entries.TryAdd(name, ($"/dev/block/by-name/{name}", null));
            }

            if (entries.Count == 0)
            {
                var mapper = await RunShellAsync(serial, "ls /dev/block/mapper", cancellationToken).ConfigureAwait(false);
                foreach (var name in DeviceOutputParser.ParsePartitionNamesFromLs(mapper))
                {
                    entries.TryAdd(name, ($"/dev/block/mapper/{name}", null));
                }
            }

            if (entries.Count > 0)
            {
                warnings.Add("未能通过 blockdev/sysfs 读取容量，已列出分区名但大小未知。");
            }
        }

        var slotTask = _adb.GetPropAsync(serial, "ro.boot.slot_suffix", cancellationToken);
        var codenameTask = _adb.GetPropAsync(serial, "ro.product.device", cancellationToken);
        await Task.WhenAll(slotTask, codenameTask).ConfigureAwait(false);

        var partitions = entries
            .Select(pair => new PartitionInfo
            {
                Name = pair.Key,
                SizeBytes = pair.Value.Size,
                Source = PartitionSource.AdbBlockByyName,
                Risk = PartitionRules.GetRisk(pair.Key),
                IsDynamic = PartitionRules.IsDynamic(pair.Key)
            })
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (partitions.Length == 0)
        {
            warnings.Add("Recovery 中未读取到分区。请确认 ADB 已授权、recovery 提供 by-name 节点，或尝试 TWRP。");
        }

        _logging.Info(
            $"Recovery 分区枚举：{partitions.Length} 个，root={isRoot}，带容量={partitions.Count(p => p.SizeBytes is > 0)}",
            "Partition");

        return new PartitionCatalog
        {
            Partitions = partitions,
            Source = PartitionSource.AdbBlockByyName,
            HasSizes = partitions.Any(p => p.SizeBytes is > 0),
            Warnings = warnings,
            CurrentSlot = string.IsNullOrWhiteSpace(slotTask.Result) ? null : slotTask.Result,
            DeviceCodename = string.IsNullOrWhiteSpace(codenameTask.Result) ? null : codenameTask.Result,
            IsUserspace = false
        };
    }

    private async Task<PartitionCatalog> ReadFastbootPartitionsAsync(string serial, CancellationToken cancellationToken)
    {
        var vars = await _fastboot.GetVarAllAsync(serial, cancellationToken).ConfigureAwait(false);
        var warnings = new List<string>();
        var partitions = new List<PartitionInfo>();

        foreach (var pair in vars)
        {
            const string prefix = "partition-size:";
            if (!pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var name = pair.Key[prefix.Length..].Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var size = ParseFastbootSize(pair.Value);
            if (size == 0)
            {
                warnings.Add($"分区 {name} 的大小为 0，已按未知处理");
                size = null;
            }

            partitions.Add(new PartitionInfo
            {
                Name = name,
                SizeBytes = size,
                Source = PartitionSource.FastbootGetVar,
                Risk = PartitionRules.GetRisk(name),
                IsDynamic = PartitionRules.IsDynamic(name)
            });
        }

        if (partitions.Count == 0)
        {
            warnings.Add("fastboot getvar all 未返回 partition-size 字段，无法校验分区容量。");
        }

        vars.TryGetValue("is-userspace", out var userspaceText);
        bool? userspace = userspaceText?.Equals("yes", StringComparison.OrdinalIgnoreCase) == true ||
                          userspaceText?.Equals("true", StringComparison.OrdinalIgnoreCase) == true
            ? true
            : userspaceText is not null ? false : null;

        vars.TryGetValue("current-slot", out var slot);
        vars.TryGetValue("product", out var product);

        // 与实际系统模式抓到的分区列表交叉校验；同一序列号在两种模式下都读取过时才提示。
        PartitionCatalog? systemCatalog = null;
        lock (_gate)
        {
            if (_cache.TryGetValue($"{serial}|{DeviceMode.System}", out var cached))
            {
                systemCatalog = cached.Catalog;
            }
        }

        if (systemCatalog is { Partitions.Count: > 0 } && partitions.Count > 0)
        {
            var systemNames = systemCatalog.Partitions.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var fastbootNames = partitions.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missing = systemNames.Except(fastbootNames, StringComparer.OrdinalIgnoreCase).Take(8).ToArray();
            var extra = fastbootNames.Except(systemNames, StringComparer.OrdinalIgnoreCase).Take(8).ToArray();
            if (missing.Length > 0 || extra.Length > 0)
            {
                warnings.Add(
                    $"系统模式与 Fastboot 分区列表存在差异（Fastboot 缺少 {missing.Length} 项，新增 {extra.Length} 项），" +
                    "可能由动态分区或 slot 切换导致，请核对后刷入。");
            }
        }

        return new PartitionCatalog
        {
            Partitions = partitions
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            Source = PartitionSource.FastbootGetVar,
            HasSizes = partitions.Any(p => p.SizeBytes is > 0),
            Warnings = warnings,
            DeviceCodename = string.IsNullOrWhiteSpace(product) ? null : product,
            IsUserspace = userspace,
            CurrentSlot = string.IsNullOrWhiteSpace(slot) ? null : slot
        };
    }

    private async Task<string> RunShellAsync(string serial, string command, CancellationToken cancellationToken)
    {
        var result = await _adb.ExecuteRawAsync(
            serial,
            ["shell", command],
            cancellationToken: cancellationToken,
            logToHistory: true,
            timeout: TimeSpan.FromSeconds(20)).ConfigureAwait(false);

        return result.CombinedOutput;
    }

    private static long? ParseFastbootSize(string value)
    {
        var text = value.Trim();
        if (text.Length == 0)
        {
            return null;
        }

        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return ulong.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex) &&
                   hex <= long.MaxValue
                ? (long)hex
                : null;
        }

        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number > 0)
        {
            return number;
        }

        // 有些 fastboot 版本输出 "0x... (16 MB)"，只取第一个 token。
        var token = text.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            ulong.TryParse(token[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var tokenHex) &&
            tokenHex <= long.MaxValue)
        {
            return (long)tokenHex;
        }

        return null;
    }
}
