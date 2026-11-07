using System.Globalization;
using System.Text.RegularExpressions;

namespace FlashKit.Core.Parsing;

public sealed record BatteryParseResult(
    int? Level,
    string? Status,
    int? TemperatureCelsius,
    int? VoltageMillivolts,
    IReadOnlyList<string> Warnings);

public sealed record MemoryParseResult(
    long? TotalBytes,
    long? AvailableBytes,
    IReadOnlyList<string> Warnings);

public sealed record StorageParseResult(
    long? TotalBytes,
    long? AvailableBytes,
    string? MountPoint,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Android 文本输出的容错解析集合。所有解析器都必须满足：输入为空、字段缺失、
/// 数值溢出、厂商附加文本时返回部分结果而不是抛异常。
/// </summary>
public static partial class DeviceOutputParser
{
    private static readonly string[] StorageMountPreference =
    [
        "/data",
        "/storage/emulated",
        "/sdcard",
        "/mnt/user/0/primary",
        "/"
    ];

    public static BatteryParseResult ParseBattery(string? output)
    {
        var warnings = new List<string>();
        if (string.IsNullOrWhiteSpace(output))
        {
            return new BatteryParseResult(null, null, null, null, ["未读取到 dumpsys battery 输出"]);
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (key.Length > 0)
            {
                values[key] = value;
            }
        }

        int? level = null;
        if (values.TryGetValue("level", out var levelText) &&
            int.TryParse(levelText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLevel))
        {
            // 个别 ROM 会输出 101（充电保护状态），钳制到 100 便于进度条显示。
            level = Math.Clamp(parsedLevel, 0, 100);
            if (parsedLevel > 100)
            {
                warnings.Add($"电量字段异常（{parsedLevel}），已按 100% 显示");
            }
        }

        string? status = null;
        if (values.TryGetValue("status", out var statusText))
        {
            status = statusText switch
            {
                "1" => "未知",
                "2" => "充电中",
                "3" => "放电中",
                "4" => "未充电",
                "5" => "已充满",
                _ => string.IsNullOrWhiteSpace(statusText) ? null : statusText
            };
        }

        if (values.TryGetValue("present", out var present) &&
            present.Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add("系统报告电池不存在");
        }

        int? temperature = null;
        if (values.TryGetValue("temperature", out var tempText) &&
            int.TryParse(tempText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tempRaw))
        {
            // dumpsys battery 的温度单位是 0.1 摄氏度。
            temperature = (int)Math.Round(tempRaw / 10.0, MidpointRounding.AwayFromZero);
        }

        int? voltage = null;
        if (values.TryGetValue("voltage", out var voltageText) &&
            int.TryParse(voltageText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var voltageRaw))
        {
            voltage = voltageRaw;
        }

        if (level is null)
        {
            warnings.Add("电池电量字段缺失");
        }

        return new BatteryParseResult(level, status, temperature, voltage, warnings);
    }

    public static MemoryParseResult ParseMemInfo(string? output)
    {
        var warnings = new List<string>();
        if (string.IsNullOrWhiteSpace(output))
        {
            return new MemoryParseResult(null, null, ["未读取到 /proc/meminfo"]);
        }

        var values = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var match = MemInfoRegex().Match(rawLine);
            if (!match.Success)
            {
                continue;
            }

            if (long.TryParse(match.Groups["value"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var kb) &&
                kb >= 0)
            {
                values[match.Groups["key"].Value] = kb;
            }
        }

        long? total = ToBytes(values, "MemTotal");
        long? available = ToBytes(values, "MemAvailable");

        if (available is null)
        {
            // 较老内核没有 MemAvailable，退化为 MemFree+Buffers+Cached，
            // 它不等价于 MemAvailable，但比直接显示“未知”更有参考价值。
            if (values.TryGetValue("MemFree", out var free))
            {
                var fallbackKb = free;
                if (values.TryGetValue("Buffers", out var buffers))
                {
                    fallbackKb += buffers;
                }

                if (values.TryGetValue("Cached", out var cached))
                {
                    fallbackKb += cached;
                }

                available = SafeKbToBytes(fallbackKb);
                warnings.Add("内核未提供 MemAvailable，已使用 MemFree+Buffers+Cached 估算");
            }
            else
            {
                warnings.Add("内存可用量字段缺失");
            }
        }

        if (total is null)
        {
            warnings.Add("内存总量字段缺失");
        }

        return new MemoryParseResult(total, available, warnings);
    }

    /// <summary>
    /// 兼容 df -k 与 df -h。优先选择 /data；若没有 /data，再按 Android 常见挂载点顺序选择。
    /// 解析失败的行直接忽略，不会让整页信息失败。
    /// </summary>
    public static StorageParseResult ParseDf(string? output)
    {
        var warnings = new List<string>();
        if (string.IsNullOrWhiteSpace(output))
        {
            return new StorageParseResult(null, null, null, ["未读取到 df 输出"]);
        }

        // df -k 的表头是 1K-blocks，数字没有单位，需要按 KiB 换算；df -h 带 K/M/G 后缀。
        var numbersAreKb = output.Contains("1K-blocks", StringComparison.OrdinalIgnoreCase) ||
                           output.Contains("1024-blocks", StringComparison.OrdinalIgnoreCase);
        var rows = new List<DfRow>();
        foreach (var rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("Filesystem", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("文件系统", StringComparison.Ordinal))
            {
                continue;
            }

            var match = DfRegex().Match(line);
            if (!match.Success)
            {
                continue;
            }

            var mount = match.Groups["mount"].Value.Trim();
            if (string.IsNullOrWhiteSpace(mount))
            {
                continue;
            }

            if (!TryParseSize(match.Groups["size"].Value, numbersAreKb, out var total) ||
                !TryParseSize(match.Groups["avail"].Value, numbersAreKb, out var available))
            {
                continue;
            }

            rows.Add(new DfRow(mount, total, available));
        }

        if (rows.Count == 0)
        {
            return new StorageParseResult(null, null, null, ["df 输出格式无法识别"]);
        }

        DfRow? selected = null;
        foreach (var preferred in StorageMountPreference)
        {
            selected = rows.FirstOrDefault(r =>
                r.Mount.Equals(preferred, StringComparison.Ordinal) ||
                (preferred != "/" && r.Mount.StartsWith(preferred, StringComparison.Ordinal)));
            if (selected is not null)
            {
                break;
            }
        }

        selected ??= rows
            .Where(r => !r.Mount.StartsWith("/proc", StringComparison.Ordinal) &&
                        !r.Mount.StartsWith("/sys", StringComparison.Ordinal) &&
                        !r.Mount.StartsWith("/dev", StringComparison.Ordinal))
            .OrderByDescending(r => r.TotalBytes)
            .FirstOrDefault();

        if (selected is null)
        {
            return new StorageParseResult(null, null, null, ["未找到可用的存储挂载点"]);
        }

        if (selected.TotalBytes <= 0)
        {
            warnings.Add($"挂载点 {selected.Mount} 的总容量为 0");
        }

        return new StorageParseResult(selected.TotalBytes, selected.AvailableBytes, selected.Mount, warnings);
    }

    public static IReadOnlyDictionary<string, string> ParseGetProp(string? output)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(output))
        {
            return result;
        }

        foreach (var rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var match = GetPropRegex().Match(rawLine.Trim());
            if (match.Success)
            {
                result[match.Groups["key"].Value] = match.Groups["value"].Value.Trim();
            }
        }

        return result;
    }

    public static IReadOnlyDictionary<string, string> ParseFastbootGetVarAll(string? output)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(output))
        {
            return result;
        }

        foreach (var rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("(bootloader)", StringComparison.OrdinalIgnoreCase))
            {
                line = line["(bootloader)".Length..].Trim();
            }
            else if (line.StartsWith("(fastboot)", StringComparison.OrdinalIgnoreCase))
            {
                line = line["(fastboot)".Length..].Trim();
            }

            if (line.Length == 0 || line.StartsWith("Finished", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("OKAY", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // fastboot 的 partition-size:boot: 0x... 键本身含冒号，因此按最后一个冒号拆分。
            var separator = line.LastIndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (key.Length > 0)
            {
                result[key] = value;
            }
        }

        return result;
    }

    public static IReadOnlyList<string> ParsePartitionNamesFromLs(string? output)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(output))
        {
            return Array.Empty<string>();
        }

        foreach (var rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 ||
                line.StartsWith("ls:", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Permission denied", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("No such file", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // ls -l 可能返回 "lrwxrwxrwx ... boot -> /dev/block/sda1"，只取目标名。
            var arrow = line.IndexOf("->", StringComparison.Ordinal);
            if (arrow > 0)
            {
                line = line[..arrow].Trim();
                var lastSpace = line.LastIndexOf(' ');
                if (lastSpace >= 0)
                {
                    line = line[(lastSpace + 1)..];
                }
            }

            if (PartitionNameRegex().IsMatch(line))
            {
                names.Add(line);
            }
        }

        return names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static long? ToBytes(IReadOnlyDictionary<string, long> values, string key) =>
        values.TryGetValue(key, out var kb) ? SafeKbToBytes(kb) : null;

    private static long? SafeKbToBytes(long kb)
    {
        try
        {
            return checked(kb * 1024L);
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static bool TryParseSize(string text, bool numbersAreKb, out long bytes)
    {
        bytes = 0;
        var value = text.Trim();
        if (value.Length == 0)
        {
            return false;
        }

        var multiplier = numbersAreKb ? 1024L : 1L;
        var suffix = char.ToUpperInvariant(value[^1]);
        if (suffix is 'K' or 'M' or 'G' or 'T' or 'P')
        {
            multiplier = suffix switch
            {
                'K' => 1024L,
                'M' => 1024L * 1024,
                'G' => 1024L * 1024 * 1024,
                'T' => 1024L * 1024 * 1024 * 1024,
                'P' => 1024L * 1024 * 1024 * 1024 * 1024,
                _ => 1L
            };
            value = value[..^1];
        }

        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return false;
        }

        try
        {
            bytes = checked((long)Math.Round(number * multiplier));
            return bytes >= 0;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private sealed record DfRow(string Mount, long TotalBytes, long AvailableBytes);

    [GeneratedRegex(@"^\s*(?<key>[A-Za-z0-9_()]+):\s*(?<value>\d+)\s*kB\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex MemInfoRegex();

    [GeneratedRegex(@"^\s*(?<fs>\S+)\s+(?<size>[\d.]+[KMGTP]?)\s+(?<used>[\d.]+[KMGTP]?)\s+(?<avail>[\d.]+[KMGTP]?)\s+(?<pct>\d+)%\s+(?<mount>.+?)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex DfRegex();

    [GeneratedRegex(@"^\[(?<key>[^\]]+)\]\s*:\s*\[(?<value>.*)\]\s*$")]
    private static partial Regex GetPropRegex();

    [GeneratedRegex(@"^[A-Za-z0-9._-]+$")]
    private static partial Regex PartitionNameRegex();
}
