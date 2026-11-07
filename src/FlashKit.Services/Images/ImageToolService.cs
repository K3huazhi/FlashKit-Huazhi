using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using FlashKit.Core.Models;
using FlashKit.Services.Payload;

namespace FlashKit.Services.Images;

public sealed class ImageToolService : IImageToolService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly SparseImageConverter _sparse = new();

    public async Task<ImageInspection> InspectAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("镜像不存在。", path);
        }

        var info = new FileInfo(path);
        var head = new byte[(int)Math.Min(4096, info.Length)];
        await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous))
        {
            await stream.ReadExactlyAsync(head, cancellationToken).ConfigureAwait(false);
        }

        if (head.Length >= 4 && head[..4].SequenceEqual("CrAU"u8.ToArray()))
        {
            return new ImageInspection
            {
                FilePath = path,
                FileSize = info.Length,
                Format = ImageFormatKind.Payload,
                Magic = "CrAU",
                Summary = "A/B payload.bin"
            };
        }

        var sparse = ImageFormats.Inspect(path);
        if (sparse.IsSparse)
        {
            uint checksum = 0;
            if (info.Length >= 28)
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var header = new byte[28];
                await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
                checksum = BitConverter.ToUInt32(header, 24);
            }

            return new ImageInspection
            {
                FilePath = path,
                FileSize = info.Length,
                Format = ImageFormatKind.Sparse,
                Magic = "0xED26FF3A",
                ExpandedSizeBytes = sparse.ExpandedSizeBytes,
                BlockSize = sparse.BlockSize,
                SparseMajor = sparse.MajorVersion,
                SparseMinor = sparse.MinorVersion,
                SparseChunks = sparse.TotalChunks,
                SparseChecksum = checksum,
                Summary = $"Android sparse v{sparse.MajorVersion}.{sparse.MinorVersion} · " +
                          $"block {sparse.BlockSize} · {sparse.TotalBlocks} blocks"
            };
        }

        if (head.Length >= 1082 && BitConverter.ToUInt16(head, 1080) == 0xEF53)
        {
            var log = head.Length >= 1052 ? BitConverter.ToUInt32(head, 1048) : 0;
            var blockSize = log <= 6 ? 1024u << (int)log : 0;
            return new ImageInspection
            {
                FilePath = path,
                FileSize = info.Length,
                Format = ImageFormatKind.Ext4,
                Magic = "0xEF53",
                BlockSize = blockSize,
                ExpandedSizeBytes = info.Length,
                Summary = $"ext4 · block {blockSize}"
            };
        }

        if (head.Length >= 1028 && BitConverter.ToUInt32(head, 1024) == 0xF2F52010)
        {
            return new ImageInspection
            {
                FilePath = path,
                FileSize = info.Length,
                Format = ImageFormatKind.F2fs,
                Magic = "0xF2F52010",
                ExpandedSizeBytes = info.Length,
                Summary = "f2fs"
            };
        }

        if (head.Length >= 1028 && BitConverter.ToUInt32(head, 1024) == 0xE0F5E1E2)
        {
            return new ImageInspection
            {
                FilePath = path,
                FileSize = info.Length,
                Format = ImageFormatKind.Erofs,
                Magic = "0xE0F5E1E2",
                ExpandedSizeBytes = info.Length,
                Summary = "erofs"
            };
        }

        return new ImageInspection
        {
            FilePath = path,
            FileSize = info.Length,
            Format = ImageFormatKind.Raw,
            Magic = head.Length >= 4 ? Convert.ToHexString(head.AsSpan(0, 4)) : string.Empty,
            ExpandedSizeBytes = info.Length,
            Summary = "raw image"
        };
    }

    public async Task<ImageHashResult> HashAsync(string path, string? expectedHash, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("镜像不存在。", path);
        }

        using var md5 = MD5.Create();
        using var sha = SHA256.Create();
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[1024 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            md5.TransformBlock(buffer, 0, read, null, 0);
            sha.TransformBlock(buffer, 0, read, null, 0);
        }

        md5.TransformFinalBlock([], 0, 0);
        sha.TransformFinalBlock([], 0, 0);
        var md5Text = Convert.ToHexString(md5.Hash!).ToLowerInvariant();
        var shaText = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
        var expected = NormalizeHash(expectedHash);
        bool? matches = expected is null
            ? null
            : expected.Equals(shaText, StringComparison.OrdinalIgnoreCase) ||
              expected.Equals(md5Text, StringComparison.OrdinalIgnoreCase);

        return new ImageHashResult
        {
            FilePath = path,
            Md5 = md5Text,
            Sha256 = shaText,
            Expected = expectedHash,
            MatchesExpected = matches,
            SizeBytes = stream.Length
        };
    }

    public async Task<ImageConversionResult> ConvertAsync(
        ImageConversionOptions options,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(options.SourcePath))
        {
            throw new FileNotFoundException("源镜像不存在。", options.SourcePath);
        }

        if (string.Equals(Path.GetFullPath(options.SourcePath), Path.GetFullPath(options.OutputPath), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("输出路径不能与源文件相同。");
        }

        var source = await InspectAsync(options.SourcePath, cancellationToken).ConfigureAwait(false);
        if (options.TargetFormat == ImageFormatKind.Raw)
        {
            if (source.Format != ImageFormatKind.Sparse)
            {
                throw new InvalidOperationException("只有 sparse 镜像可以转换为 raw。");
            }

            await _sparse.ConvertToRawAsync(options.SourcePath, options.OutputPath, progress, cancellationToken).ConfigureAwait(false);
        }
        else if (options.TargetFormat == ImageFormatKind.Sparse)
        {
            if (source.Format is not (ImageFormatKind.Raw or ImageFormatKind.Ext4 or ImageFormatKind.F2fs or ImageFormatKind.Erofs))
            {
                throw new InvalidOperationException("只有 raw/文件系统镜像可以转换为 sparse。");
            }

            await _sparse.ConvertToSparseAsync(options.SourcePath, options.OutputPath, progress, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            throw new InvalidOperationException("目标格式必须是 raw 或 sparse。");
        }

        return new ImageConversionResult
        {
            OutputPath = options.OutputPath,
            SizeBytes = new FileInfo(options.OutputPath).Length,
            SourceFormat = source.Format,
            TargetFormat = options.TargetFormat
        };
    }

    public async Task<ImagePayloadResult> ExtractPayloadAsync(
        string payloadPath,
        string outputDirectory,
        IProgress<OperationProgress>? progress = null,
        Action<string>? logLine = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(payloadPath))
        {
            throw new FileNotFoundException("payload.bin 不存在。", payloadPath);
        }

        Directory.CreateDirectory(outputDirectory);
        var imagesDirectory = Path.Combine(outputDirectory, "images");
        Directory.CreateDirectory(imagesDirectory);
        var created = !Directory.EnumerateFiles(imagesDirectory).Any();
        try
        {
            var extractor = new PayloadExtractor();
            var result = await extractor.ExtractAsync(
                payloadPath,
                imagesDirectory,
                progress,
                logLine,
                cancellationToken).ConfigureAwait(false);

            var manifest = new
            {
                manifestVersion = "1",
                toolVersion = AppInfo.Version,
                format = "payload.bin",
                source = Path.GetFullPath(payloadPath),
                extractedAt = DateTimeOffset.Now,
                images = result.Images.Select(i => new
                {
                    partitionName = i.PartitionName,
                    fileName = i.FileName,
                    sizeBytes = i.SizeBytes,
                    sourceOperations = i.SourceOperations
                }),
                warnings = result.Warnings
            };
            await File.WriteAllTextAsync(
                Path.Combine(outputDirectory, "manifest.json"),
                JsonSerializer.Serialize(manifest, JsonOptions),
                cancellationToken).ConfigureAwait(false);

            return new ImagePayloadResult
            {
                OutputDirectory = outputDirectory,
                ImagesDirectory = imagesDirectory,
                Images = result.Images,
                Warnings = result.Warnings
            };
        }
        catch
        {
            if (created)
            {
                try
                {
                    Directory.Delete(imagesDirectory, recursive: true);
                }
                catch
                {
                }
            }

            throw;
        }
    }

    private static string? NormalizeHash(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            text = text[2..];
        }

        return text.Replace(" ", string.Empty).Replace(":", string.Empty);
    }
}
