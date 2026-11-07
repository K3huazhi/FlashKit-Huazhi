namespace FlashKit.Core.Models;

public enum FileSystemFormat
{
    Unknown = 0,
    Raw,
    Sparse,
    Ext4,
    F2fs,
    Erofs,
    Payload
}

public sealed class FileSystemCapabilities
{
    public required string Serial { get; init; }
    public bool ErofsUnpack { get; init; }
    public bool Ext4Unpack { get; init; }
    public bool Ext4Pack { get; init; }
    public bool F2fsUnpack { get; init; }
    public bool F2fsPack { get; init; }
    public bool ErofsPack { get; init; }
    public string Toolchain { get; init; } = "未检测";
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

public sealed class FileSystemUnpackRequest
{
    public required string Serial { get; init; }
    public required string SourcePath { get; init; }
    public string? OutputDirectory { get; init; }
}

public sealed class FileSystemPackRequest
{
    public required string Serial { get; init; }
    public required string SourceDirectory { get; init; }
    public required string OutputImagePath { get; init; }
    public FileSystemFormat Format { get; init; } = FileSystemFormat.Ext4;
    public long? PartitionSizeBytes { get; init; }
    public string? MetadataPath { get; init; }
}

public sealed class FileSystemMetadataEntry
{
    public required string RelativePath { get; init; }
    public string Kind { get; init; } = "file";
    public string Permissions { get; init; } = string.Empty;
    public string Mode { get; init; } = string.Empty;
    public int Uid { get; init; }
    public int Gid { get; init; }
    public long SizeBytes { get; init; }
    public string? LinkTarget { get; init; }
    public string? SelinuxContext { get; init; }
    public string? Capabilities { get; init; }
}

public sealed class FileSystemUnpackResult
{
    public required string OutputDirectory { get; init; }
    public required string FilesDirectory { get; init; }
    public required FileSystemFormat Format { get; init; }
    public required int FileCount { get; init; }
    public required string ManifestPath { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

public interface IFileSystemService
{
    Task<FileSystemCapabilities> CheckCapabilitiesAsync(string serial, CancellationToken cancellationToken = default);

    Task<FileSystemUnpackResult> UnpackAsync(
        FileSystemUnpackRequest request,
        IProgress<OperationProgress>? progress = null,
        Action<string>? logLine = null,
        CancellationToken cancellationToken = default);

    Task<string> PackAsync(
        FileSystemPackRequest request,
        IProgress<OperationProgress>? progress = null,
        Action<string>? logLine = null,
        CancellationToken cancellationToken = default);
}
