namespace FlashKit.Core.Models;

public enum BackupPartitionGroup
{
    Regular = 0,
    SuperLogical,
    HighRisk
}

public enum BackupTransport
{
    BootRoot = 0,
    Twrp
}

public sealed record BackupPartitionSelection(
    string PartitionName,
    long? SizeBytes,
    PartitionRisk Risk,
    BackupPartitionGroup Group,
    bool IsSuperContainer = false,
    string? SourceBlockPath = null);

public sealed class BackupImageEntry
{
    public required string PartitionName { get; init; }
    public required string FileName { get; init; }
    public long SizeBytes { get; init; }
    public PartitionRisk Risk { get; init; }
    public string? SourceBlockPath { get; init; }
    public string? Md5 { get; init; }
}

public sealed class BackupManifest
{
    public string ManifestVersion { get; init; } = "1";
    public required string ToolVersion { get; init; }
    public required string Serial { get; init; }
    public string? DeviceCodename { get; init; }
    public string? DeviceModel { get; init; }
    public string? AndroidRelease { get; init; }
    public string? TwrpVersion { get; init; }
    public string Mode { get; init; } = "Fastboot";
    public bool IsSuperWholeBackup { get; init; }
    public bool DdUsedNoErrorSync { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public required string OutputDirectory { get; init; }
    public bool IsSelective { get; init; } = true;
    public IReadOnlyList<BackupImageEntry> Images { get; init; } = Array.Empty<BackupImageEntry>();
    public IReadOnlyList<string> NotBackedUpPartitions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> CoveredBySuperPartitions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> FailedPartitions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public long TotalBytes { get; init; }
    public bool Cancelled { get; init; }
}

public sealed class BackupRunOptions
{
    public required string Serial { get; init; }
    public required string DestinationRoot { get; init; }
    public required IReadOnlyList<BackupPartitionSelection> AllPartitions { get; init; }
    public required IReadOnlyList<BackupPartitionSelection> SelectedPartitions { get; init; }
    public string? DeviceCodename { get; init; }
    public string? DeviceModel { get; init; }
    public string? AndroidRelease { get; init; }
    public string? TwrpVersion { get; init; }
    public BackupTransport Transport { get; init; } = BackupTransport.BootRoot;
    public bool IsUserspace { get; init; }
}

public sealed class BackupEnvironmentInfo
{
    public required BackupTransport Transport { get; init; }
    public required bool IsSupported { get; init; }
    public required bool IsRoot { get; init; }
    public string? TwrpVersion { get; init; }
    public string? AndroidRelease { get; init; }
    public string? Codename { get; init; }
    public string? Model { get; init; }
    public required string Message { get; init; }
    public PartitionCatalog? Catalog { get; init; }
    public string ModeText => Transport == BackupTransport.Twrp ? "TWRP Recovery" : "开机 Root / KSU";
}

public sealed class BackupProgress
{
    public int CurrentIndex { get; init; }
    public int TotalCount { get; init; }
    public string? PartitionName { get; init; }
    public double Percentage { get; init; }
    public long BytesProcessed { get; init; }
    public long? TotalBytes { get; init; }
    public string Status { get; init; } = string.Empty;
}

public sealed class BackupResult
{
    public required string OutputDirectory { get; init; }
    public required BackupManifest Manifest { get; init; }
    public string? ManifestPath { get; init; }
    public int BackedUpCount => Manifest.Images.Count;
    public int NotBackedUpCount => Manifest.NotBackedUpPartitions.Count;
    public bool HasFailures => Manifest.FailedPartitions.Count > 0;
    public string Summary => $"已备份 {BackedUpCount} 个分区，未包含 {NotBackedUpCount} 个分区";
}

public sealed class BackupException : Exception
{
    public BackupException(string message) : base(message)
    {
    }

    public BackupException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
