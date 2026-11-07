namespace FlashKit.Core.Models;

public sealed class RestoreRequest
{
    public required string ManifestPath { get; init; }
    public required string Serial { get; init; }
    public required DeviceMode DeviceMode { get; init; }
    public IReadOnlyList<string> SelectedPartitions { get; init; } = Array.Empty<string>();
    public bool ContinueOnError { get; init; }
    public bool AllowModelMismatch { get; init; }
    public bool FastbootUserspace { get; init; }
}

public sealed class RestoreProgress
{
    public int CurrentIndex { get; init; }
    public int TotalCount { get; init; }
    public string? PartitionName { get; init; }
    public double Percentage { get; init; }
    public string Status { get; init; } = string.Empty;
}

public sealed class RestoreItemResult
{
    public required string PartitionName { get; init; }
    public bool Success { get; init; }
    public int ExitCode { get; init; }
    public string Message { get; init; } = string.Empty;
}

public sealed class RestoreResult
{
    public required string ManifestPath { get; init; }
    public required string BackupDirectory { get; init; }
    public required IReadOnlyList<RestoreItemResult> Results { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public bool Cancelled { get; init; }
    public int SuccessCount => Results.Count(r => r.Success);
    public int FailureCount => Results.Count(r => !r.Success);
    public string Summary => $"已还原 {SuccessCount} 个分区，失败 {FailureCount} 个";
}

public interface IRestoreService
{
    Task<BackupManifest> ReadManifestAsync(string manifestPath, CancellationToken cancellationToken = default);
    Task<RestoreResult> RestoreAsync(
        RestoreRequest request,
        IProgress<RestoreProgress>? progress = null,
        Action<ProcessOutputLine>? onOutput = null,
        CancellationToken cancellationToken = default);
}
