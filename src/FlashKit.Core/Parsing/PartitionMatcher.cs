using FlashKit.Core.Models;

namespace FlashKit.Core.Parsing;

public static class PartitionMatcher
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".img", ".image", ".bin"
    };

    public static bool IsImageFile(string path) =>
        ImageExtensions.Contains(Path.GetExtension(path));

    /// <summary>
    /// 从文件名推导分区名。先做完整匹配，再去除 slot 后缀匹配，避免
    /// "vendor_boot.img" 被错误匹配到 "boot"。无法确认时返回 null，由 UI 标黄要求手动指定。
    /// </summary>
    public static string? InferPartitionName(string filePath, IEnumerable<string> knownPartitions)
    {
        var partitions = knownPartitions
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (partitions.Length == 0)
        {
            return null;
        }

        var baseName = Path.GetFileNameWithoutExtension(filePath).Trim();
        if (baseName.Length == 0)
        {
            return null;
        }

        var exact = partitions.FirstOrDefault(p => p.Equals(baseName, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        var normalizedBase = PartitionRules.StripSlotSuffix(baseName);
        var normalizedMatches = partitions
            .Where(p => PartitionRules.StripSlotSuffix(p).Equals(normalizedBase, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (normalizedMatches.Length == 1)
        {
            return normalizedMatches[0];
        }

        // 同时存在 boot_a / boot_b 时，优先选择与文件名 slot 一致的项。
        if (normalizedMatches.Length > 1)
        {
            var suffix = baseName.EndsWith("_a", StringComparison.OrdinalIgnoreCase) ? "_a" :
                baseName.EndsWith("_b", StringComparison.OrdinalIgnoreCase) ? "_b" : null;
            if (suffix is not null)
            {
                return normalizedMatches.FirstOrDefault(p =>
                    p.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
            }
        }

        return null;
    }

    public static bool IsKnownPartition(string partitionName, IEnumerable<string> knownPartitions) =>
        knownPartitions.Any(p => p.Equals(partitionName, StringComparison.OrdinalIgnoreCase));
}
