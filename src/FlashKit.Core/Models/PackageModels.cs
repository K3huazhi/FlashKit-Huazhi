using System.Text.Json.Serialization;

namespace FlashKit.Core.Models;

public enum PackageFormat
{
    Unknown = 0,
    PayloadBin,
    ZipImages,
    DatBr
}

public sealed record PackageSourceInfo
{
    public required string FileName { get; init; }
    public required string FullPath { get; init; }
    public long SizeBytes { get; init; }
    public PackageFormat Format { get; init; }
    public DateTimeOffset LastWriteTime { get; init; }
}

public sealed class ManifestImageEntry
{
    public required string PartitionName { get; init; }
    public required string FileName { get; init; }
    public long SizeBytes { get; init; }
    /// <summary>payload.bin 原始 InstallOperation 类型，例如 REPLACE_XZ；ZIP 直解为 "RAW"。</summary>
    public IReadOnlyList<string> SourceOperations { get; init; } = Array.Empty<string>();
    public string? Sha256 { get; init; }
}

public sealed class PackageManifest
{
    public string ManifestVersion { get; init; } = "1";
    public required string ToolVersion { get; init; }
    public required string Format { get; init; }
    public required PackageSourceInfo Source { get; init; }
    public DateTimeOffset ExtractedAt { get; init; } = DateTimeOffset.Now;
    public string OutputDirectory { get; init; } = string.Empty;
    public IReadOnlyList<ManifestImageEntry> Images { get; init; } = Array.Empty<ManifestImageEntry>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public bool IsIncremental { get; init; }
    public IReadOnlyDictionary<string, string> Extra { get; init; } = new Dictionary<string, string>();
}

public sealed class ExtractResult
{
    public required string OutputDirectory { get; init; }
    public required PackageManifest Manifest { get; init; }
    public string? ManifestPath { get; init; }
    public IReadOnlyList<string> Warnings => Manifest.Warnings;
    public int ImageCount => Manifest.Images.Count;
}

public sealed class PackImageInput
{
    public required string FilePath { get; init; }
    public required string PartitionName { get; init; }
}

public sealed record PackResult(
    string OutputPath,
    long SizeBytes,
    int ImageCount,
    IReadOnlyList<string> Warnings);

public sealed class PackValidationResult
{
    public bool IsValid { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

public sealed class IncrementalOtaException : Exception
{
    public IncrementalOtaException(string message) : base(message)
    {
    }
}

public sealed class PackageFormatException : Exception
{
    public PackageFormatException(string message) : base(message)
    {
    }

    public PackageFormatException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

public static class AppInfo
{
    public const string Version = "2.2.0";
    public const string AppName = "FlashKit 安卓刷机工具箱";
}






