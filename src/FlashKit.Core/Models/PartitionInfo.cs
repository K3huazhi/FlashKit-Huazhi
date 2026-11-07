namespace FlashKit.Core.Models;

public sealed record PartitionInfo
{
    public required string Name { get; init; }
    public long? SizeBytes { get; init; }
    public PartitionSource Source { get; init; } = PartitionSource.AdbBlockByyName;
    public PartitionRisk Risk { get; init; } = PartitionRisk.Normal;
    public bool IsDynamic { get; init; }
    public string? BlockPath { get; init; }

    public string SizeDisplay => SizeBytes is > 0 ? FormatBytes(SizeBytes.Value) : "大小未知";

    public string RiskText => Risk switch
    {
        PartitionRisk.Critical => "高危",
        PartitionRisk.DataLoss => "清数据",
        _ => string.Empty
    };

    public static string FormatBytes(long bytes)
    {
        if (bytes < 0)
        {
            return "未知";
        }

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var index = 0;
        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }

        return index == 0 ? $"{bytes} B" : $"{value:0.##} {units[index]}";
    }
}

public sealed class PartitionCatalog
{
    public required IReadOnlyList<PartitionInfo> Partitions { get; init; }
    public PartitionSource Source { get; init; }
    public bool HasSizes { get; init; }
    public DateTimeOffset RetrievedAt { get; init; } = DateTimeOffset.Now;
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public string? DeviceCodename { get; init; }
    public bool? IsUserspace { get; init; }
    public string? CurrentSlot { get; init; }
}

public static class PartitionRules
{
    private static readonly HashSet<string> CriticalPartitions = new(StringComparer.OrdinalIgnoreCase)
    {
        "persist", "modem", "modemst1", "modemst2", "fsg", "fsc", "efs",
        "xbl", "xbl_config", "xbl_ramdump", "abl", "tz", "hyp", "keymaster",
        "keystore", "devcfg", "dsp", "rpm", "pmic", "sbl1", "bootloader",
        "radio", "super", "devinfo", "oem", "misc"
    };

    private static readonly HashSet<string> DynamicPartitions = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "system_ext", "product", "vendor", "odm", "vendor_dlkm",
        "system_dlkm", "odm_dlkm", "my_product", "my_stock", "my_heytap",
        "my_carrier", "my_company", "my_preload", "my_bigball"
    };

    private static readonly HashSet<string> DataLossPartitions = new(StringComparer.OrdinalIgnoreCase)
    {
        "userdata", "metadata", "cache"
    };

    public static string StripSlotSuffix(string partitionName)
    {
        if (string.IsNullOrWhiteSpace(partitionName))
        {
            return partitionName;
        }

        if (partitionName.Length > 2 &&
            (partitionName.EndsWith("_a", StringComparison.OrdinalIgnoreCase) ||
             partitionName.EndsWith("_b", StringComparison.OrdinalIgnoreCase)))
        {
            return partitionName[..^2];
        }

        return partitionName;
    }

    public static PartitionRisk GetRisk(string partitionName)
    {
        var normalized = StripSlotSuffix(partitionName);
        if (CriticalPartitions.Contains(normalized))
        {
            return PartitionRisk.Critical;
        }

        return DataLossPartitions.Contains(normalized) ? PartitionRisk.DataLoss : PartitionRisk.Normal;
    }

    public static bool IsDynamic(string partitionName) =>
        DynamicPartitions.Contains(StripSlotSuffix(partitionName));

    public static int GetRecommendedOrder(string partitionName)
    {
        var normalized = StripSlotSuffix(partitionName).ToLowerInvariant();
        return normalized switch
        {
            "boot" => 10,
            "dtbo" => 20,
            "vendor_boot" => 30,
            "init_boot" => 35,
            "vbmeta" => 40,
            "vbmeta_system" => 41,
            "vbmeta_vendor" => 42,
            "system" => 50,
            "system_ext" => 51,
            "product" => 52,
            "vendor" => 53,
            "odm" => 54,
            _ => 100
        };
    }

    public static IReadOnlyList<string> CriticalPartitionNames =>
        CriticalPartitions.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
}
