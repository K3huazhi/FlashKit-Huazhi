using System.IO.Compression;
using System.Text.Encodings.Web;
using System.Text.Json;
using FlashKit.Core.Abstractions;
using FlashKit.Core.Models;
using FlashKit.Services.Payload;

namespace FlashKit.Services.Packages;

/// <summary>
/// 解包格式分派器。输出结构固定为：
///   &lt;ZIP 同名目录&gt;\images\*.img
///   &lt;ZIP 同名目录&gt;\META-INF\...
///   &lt;ZIP 同名目录&gt;\manifest.json
/// 任何失败都会清理本次创建的部分输出，避免用户误把半成品当作完整镜像。
/// </summary>
public sealed class PackageExtractor : IPackageExtractor
{
    private readonly ILoggingService _logging;
    private readonly PayloadExtractor _payloadExtractor = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public PackageExtractor(ILoggingService logging)
    {
        _logging = logging;
    }

    public async Task<ExtractResult> ExtractAsync(
        string packagePath,
        IProgress<OperationProgress>? progress = null,
        Action<string>? logLine = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(packagePath))
        {
            throw new PackageFormatException($"文件不存在：{packagePath}");
        }

        var fullPath = Path.GetFullPath(packagePath);
        var sourceDirectory = Path.GetDirectoryName(fullPath) ?? throw new PackageFormatException("无法确定源文件目录");
        var baseName = Path.GetFileNameWithoutExtension(fullPath);
        var outputDirectory = Path.Combine(sourceDirectory, baseName);

        if (Directory.Exists(outputDirectory) &&
            Directory.EnumerateFileSystemEntries(outputDirectory).Any())
        {
            throw new PackageFormatException(
                $"输出目录已存在且非空：{outputDirectory}\n" +
                "为避免覆盖，请先重命名或移动旧目录，再重新解包。");
        }

        var createdOutput = !Directory.Exists(outputDirectory);
        Directory.CreateDirectory(outputDirectory);
        Directory.CreateDirectory(Path.Combine(outputDirectory, "images"));
        Directory.CreateDirectory(Path.Combine(outputDirectory, "META-INF"));

        var warnings = new List<string>();
        string? tempPayloadDirectory = null;
        try
        {
            var detection = await DetectFormatAsync(fullPath, cancellationToken).ConfigureAwait(false);
            var sourceInfo = new PackageSourceInfo
            {
                FileName = Path.GetFileName(fullPath),
                FullPath = fullPath,
                SizeBytes = new FileInfo(fullPath).Length,
                Format = detection.Format,
                LastWriteTime = File.GetLastWriteTime(fullPath)
            };

            IReadOnlyList<ManifestImageEntry> images;
            var extra = new Dictionary<string, string>();
            var formatText = detection.Format switch
            {
                PackageFormat.PayloadBin => "payload.bin（A/B OTA）",
                PackageFormat.ZipImages => "纯 img ZIP",
                _ => "未知"
            };

            if (detection.Format == PackageFormat.PayloadBin)
            {
                string payloadPath;
                if (detection.IsInsideZip)
                {
                    tempPayloadDirectory = Path.Combine(
                        Path.GetTempPath(), "FlashKit", "payload-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(tempPayloadDirectory);
                    payloadPath = Path.Combine(tempPayloadDirectory, "payload.bin");
                    logLine?.Invoke("正在从 ZIP 中流式提取 payload.bin…");

                    await CopyPayloadEntryAsync(
                        fullPath,
                        detection.PayloadEntryName!,
                        payloadPath,
                        progress,
                        cancellationToken).ConfigureAwait(false);

                    await ZipImagesExtractor.CopyMetaInfAsync(
                        fullPath,
                        outputDirectory,
                        logLine,
                        cancellationToken).ConfigureAwait(false);
                    extra["container"] = "zip";
                }
                else
                {
                    payloadPath = fullPath;
                }

                var innerProgress = progress is null
                    ? null
                    : new Progress<OperationProgress>(p => progress.Report(new OperationProgress
                    {
                        Percentage = detection.IsInsideZip ? 10 + p.Percentage * 0.9 : p.Percentage,
                        Status = p.Status,
                        BytesProcessed = p.BytesProcessed,
                        TotalBytes = p.TotalBytes,
                        CurrentItem = p.CurrentItem
                    }));

                var payloadResult = await _payloadExtractor.ExtractAsync(
                    payloadPath,
                    Path.Combine(outputDirectory, "images"),
                    innerProgress,
                    logLine,
                    cancellationToken).ConfigureAwait(false);

                images = payloadResult.Images;
                warnings.AddRange(payloadResult.Warnings);
                extra["payloadMajorVersion"] = payloadResult.Header.Version.ToString();
                extra["payloadBlockSize"] = payloadResult.Manifest.BlockSize.ToString();
                extra["payloadMinorVersion"] = payloadResult.Manifest.MinorVersion.ToString();
                extra["payloadPartitionCount"] = payloadResult.Manifest.Partitions.Count.ToString();
                extra["incremental"] = "false";
            }
            else if (detection.Format == PackageFormat.ZipImages)
            {
                var zipResult = await new ZipImagesExtractor().ExtractAsync(
                    fullPath,
                    outputDirectory,
                    progress,
                    logLine,
                    cancellationToken).ConfigureAwait(false);
                images = zipResult.Images;
                warnings.AddRange(zipResult.Warnings);
            }
            else
            {
                throw new NotSupportedException(
                    ".dat.br / .new.dat 解包将在后续版本提供。当前版本请使用 payload.bin 或纯 img ZIP。");
            }

            var manifest = new PackageManifest
            {
                ToolVersion = AppInfo.Version,
                Format = formatText,
                Source = sourceInfo,
                OutputDirectory = outputDirectory,
                ExtractedAt = DateTimeOffset.Now,
                Images = images,
                Warnings = warnings,
                IsIncremental = false,
                Extra = extra
            };

            var manifestPath = Path.Combine(outputDirectory, "manifest.json");
            await File.WriteAllTextAsync(
                manifestPath,
                JsonSerializer.Serialize(manifest, JsonOptions),
                cancellationToken).ConfigureAwait(false);

            foreach (var warning in warnings)
            {
                _logging.Warn(warning, "Package");
            }

            progress?.Report(new OperationProgress
            {
                Percentage = 100,
                Status = "解包完成",
                BytesProcessed = images.Sum(i => i.SizeBytes),
                TotalBytes = images.Sum(i => i.SizeBytes)
            });
            logLine?.Invoke($"解包完成：{images.Count} 个镜像，输出到 {outputDirectory}");

            return new ExtractResult
            {
                OutputDirectory = outputDirectory,
                Manifest = manifest,
                ManifestPath = manifestPath
            };
        }
        catch (Exception ex)
        {
            _logging.Error($"解包失败：{ex.Message}", "Package");
            if (createdOutput)
            {
                TryDeleteDirectory(outputDirectory);
            }

            throw;
        }
        finally
        {
            if (tempPayloadDirectory is not null)
            {
                TryDeleteDirectory(tempPayloadDirectory);
            }
        }
    }

    private static async Task<DetectionResult> DetectFormatAsync(string path, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(path);
        if (extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var archive = ZipFile.OpenRead(path);
            var payloadEntry = archive.Entries.FirstOrDefault(e =>
                e.FullName.Replace('\\', '/').Equals("payload.bin", StringComparison.OrdinalIgnoreCase)) ??
                archive.Entries.FirstOrDefault(e =>
                    e.FullName.Replace('\\', '/').EndsWith("/payload.bin", StringComparison.OrdinalIgnoreCase));

            if (payloadEntry is not null)
            {
                return new DetectionResult(PackageFormat.PayloadBin, true, payloadEntry.FullName);
            }

            if (archive.Entries.Any(e => e.FullName.EndsWith(".img", StringComparison.OrdinalIgnoreCase)))
            {
                return new DetectionResult(PackageFormat.ZipImages, true, null);
            }

            if (archive.Entries.Any(e =>
                    e.FullName.EndsWith(".dat.br", StringComparison.OrdinalIgnoreCase) ||
                    e.FullName.EndsWith(".new.dat", StringComparison.OrdinalIgnoreCase)))
            {
                return new DetectionResult(PackageFormat.DatBr, true, null);
            }

            throw new PackageFormatException("ZIP 中既没有 payload.bin，也没有 .img 文件。");
        }

        if (extension.Equals(".br", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".dat", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".new.dat", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".dat.br", StringComparison.OrdinalIgnoreCase))
        {
            return new DetectionResult(PackageFormat.DatBr, false, null);
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.Asynchronous);
        var magic = new byte[4];
        await stream.ReadExactlyAsync(magic, cancellationToken).ConfigureAwait(false);
        if (magic[0] == 'C' && magic[1] == 'r' && magic[2] == 'A' && magic[3] == 'U')
        {
            return new DetectionResult(PackageFormat.PayloadBin, false, null);
        }

        throw new PackageFormatException(
            "无法识别包格式。支持：包含 payload.bin 的 ZIP、payload.bin、纯 img ZIP；" +
            ".dat.br/.new.dat 仅预留接口。");
    }

    private static async Task CopyPayloadEntryAsync(
        string zipPath,
        string entryName,
        string destination,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var entry = archive.GetEntry(entryName)
            ?? throw new PackageFormatException($"ZIP 中找不到 {entryName}");
        await using var source = entry.Open();
        await using var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous);
        var buffer = new byte[1024 * 1024];
        long copied = 0;
        var lastReportedPercent = -1.0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            copied += read;
            if (entry.Length > 0)
            {
                var percent = Math.Clamp(copied * 10.0 / entry.Length, 0, 10);
                // 7GB 的 payload 按 1MiB 上报会产生 7000+ 次 UI 消息，按 0.1% 节流。
                if (percent - lastReportedPercent >= 0.1)
                {
                    lastReportedPercent = percent;
                    progress?.Report(new OperationProgress
                    {
                        Percentage = percent,
                        Status = "从 ZIP 提取 payload.bin",
                        BytesProcessed = copied,
                        TotalBytes = entry.Length
                    });
                }
            }
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // 清理失败不应覆盖原始异常。
        }
    }

    private sealed record DetectionResult(PackageFormat Format, bool IsInsideZip, string? PayloadEntryName);
}
