using FlashKit.Core.Abstractions;
using FlashKit.Core.Models;

namespace FlashKit.Core.Models;

public sealed class DeviceFileEntry
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public bool IsDirectory { get; init; }
    public bool IsLink { get; init; }
    public long SizeBytes { get; init; }
    public string Permissions { get; init; } = string.Empty;
    public string Owner { get; init; } = string.Empty;
    public string Group { get; init; } = string.Empty;
    public DateTimeOffset? ModifiedAt { get; init; }
    public string SizeText => IsDirectory ? "—" : PartitionInfo.FormatBytes(SizeBytes);
    public string ModifiedText => ModifiedAt?.LocalDateTime.ToString("yyyy-MM-dd HH:mm") ?? "—";
    public string PermissionsText => Permissions;
}

public sealed class FileTransferProgress
{
    public double Percentage { get; init; }
    public string Status { get; init; } = string.Empty;
}

public sealed class FileTransferResult
{
    public required string LocalPath { get; init; }
    public required string RemotePath { get; init; }
    public int ExitCode { get; init; }
    public string Message { get; init; } = string.Empty;
    public bool Cancelled { get; init; }
}

public interface IDeviceFileService
{
    Task<IReadOnlyList<DeviceFileEntry>> ListAsync(string serial, string path, CancellationToken cancellationToken = default);
    Task<FileTransferResult> PullAsync(
        string serial, string remotePath, string localPath,
        Action<FileTransferProgress>? progress = null,
        CancellationToken cancellationToken = default);
    Task<FileTransferResult> PushAsync(
        string serial, string localPath, string remotePath,
        Action<FileTransferProgress>? progress = null,
        CancellationToken cancellationToken = default);
    bool IsSafeWritePath(string remotePath, out string reason);
}
