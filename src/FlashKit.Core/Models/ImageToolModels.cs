namespace FlashKit.Core.Models;

public enum ImageFormatKind
{
    Unknown = 0,
    Raw,
    Sparse,
    Ext4,
    F2fs,
    Erofs,
    Payload
}

public sealed class ImageInspection
{
    public required string FilePath { get; init; }
    public long FileSize { get; init; }
    public ImageFormatKind Format { get; init; }
    public string Magic { get; init; } = string.Empty;
    public long? ExpandedSizeBytes { get; init; }
    public uint? BlockSize { get; init; }
    public uint? SparseMajor { get; init; }
    public uint? SparseMinor { get; init; }
    public uint? SparseChunks { get; init; }
    public uint? SparseChecksum { get; init; }
    public string? FileSystemFeatures { get; init; }
    public string Summary { get; init; } = string.Empty;
}

public sealed class ImageHashResult
{
    public required string FilePath { get; init; }
    public required string Md5 { get; init; }
    public required string Sha256 { get; init; }
    public string? Expected { get; init; }
    public bool? MatchesExpected { get; init; }
    public long SizeBytes { get; init; }
}

public sealed class ImageConversionOptions
{
    public required string SourcePath { get; init; }
    public required string OutputPath { get; init; }
    public required ImageFormatKind TargetFormat { get; init; }
}

public sealed class ImageConversionResult
{
    public required string OutputPath { get; init; }
    public long SizeBytes { get; init; }
    public ImageFormatKind SourceFormat { get; init; }
    public ImageFormatKind TargetFormat { get; init; }
}

public sealed class ImagePayloadResult
{
    public required string OutputDirectory { get; init; }
    public required string ImagesDirectory { get; init; }
    public required IReadOnlyList<ManifestImageEntry> Images { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

public interface IImageToolService
{
    Task<ImageInspection> InspectAsync(string path, CancellationToken cancellationToken = default);
    Task<ImageHashResult> HashAsync(string path, string? expectedHash, CancellationToken cancellationToken = default);
    Task<ImageConversionResult> ConvertAsync(
        ImageConversionOptions options,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);
    Task<ImagePayloadResult> ExtractPayloadAsync(
        string payloadPath,
        string outputDirectory,
        IProgress<OperationProgress>? progress = null,
        Action<string>? logLine = null,
        CancellationToken cancellationToken = default);
}
