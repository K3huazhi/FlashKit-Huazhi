using System.IO.Compression;
using FlashKit.Core.Models;

namespace FlashKit.Services.Packages;

internal sealed record ZipExtractionResult(
    IReadOnlyList<ManifestImageEntry> Images,
    IReadOnlyList<string> Warnings,
    bool MetaInfFound);

/// <summary>
/// 纯 img 直塞 ZIP 解包：img 统一落到 images/，META-INF 原样保留。
/// 不把 img 散落在源包目录，也不会覆盖同名文件（自动加 _1/_2 后缀）。
/// </summary>
internal sealed class ZipImagesExtractor
{
    private const int BufferSize = 1024 * 1024;

    public async Task<ZipExtractionResult> ExtractAsync(
        string zipPath,
        string outputDirectory,
        IProgress<OperationProgress>? progress = null,
        Action<string>? logLine = null,
        CancellationToken cancellationToken = default)
    {
        var imagesDirectory = Path.Combine(outputDirectory, "images");
        Directory.CreateDirectory(imagesDirectory);
        var warnings = new List<string>();
        var images = new List<ManifestImageEntry>();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var metaInfFound = false;

        using var archive = ZipFile.OpenRead(zipPath);
        var totalBytes = archive.Entries
            .Where(e => e.Length > 0 && IsImageEntry(e))
            .Sum(e => e.Length);

        long processed = 0;
        var lastReportedPercent = -1.0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.FullName.EndsWith('/') || entry.Length == 0 && string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            var normalized = entry.FullName.Replace('\\', '/');
            if (normalized.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase))
            {
                metaInfFound = true;
                await ExtractMetaInfEntryAsync(entry, outputDirectory, warnings, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (!IsImageEntry(entry))
            {
                if (!normalized.Equals("payload.bin", StringComparison.OrdinalIgnoreCase))
                {
                    warnings.Add($"已忽略非镜像文件：{normalized}");
                }
                continue;
            }

            var sourceName = Path.GetFileName(normalized);
            var baseName = Path.GetFileNameWithoutExtension(sourceName);
            var safeBase = SanitizeFileName(baseName);
            var candidate = safeBase + ".img";
            var suffix = 1;
            while (!usedNames.Add(candidate))
            {
                candidate = $"{safeBase}_{suffix++}.img";
                warnings.Add($"镜像重名，已重命名为 {candidate}");
            }

            var destination = Path.Combine(imagesDirectory, candidate);
            await using (var source = entry.Open())
            await using (var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous))
            {
                var buffer = new byte[BufferSize];
                while (true)
                {
                    var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    processed += read;
                    var percent = totalBytes > 0 ? Math.Clamp(processed * 100.0 / totalBytes, 0, 100) : 0;
                    if (percent - lastReportedPercent < 0.2)
                    {
                        continue;
                    }

                    lastReportedPercent = percent;
                    progress?.Report(new OperationProgress
                    {
                        Percentage = percent,
                        Status = $"正在提取 {sourceName}",
                        BytesProcessed = processed,
                        TotalBytes = totalBytes,
                        CurrentItem = sourceName
                    });
                }
            }

            var length = new FileInfo(destination).Length;
            images.Add(new ManifestImageEntry
            {
                PartitionName = baseName,
                FileName = candidate,
                SizeBytes = length,
                SourceOperations = ["RAW"]
            });
            logLine?.Invoke($"已提取 {sourceName} → images/{candidate}（{PartitionInfo.FormatBytes(length)}）");
        }

        if (images.Count == 0)
        {
            throw new PackageFormatException("ZIP 中没有找到 .img 文件，也不是包含 payload.bin 的 A/B OTA 包。");
        }

        if (!metaInfFound)
        {
            warnings.Add("ZIP 中未找到 META-INF 目录，已创建空目录。");
        }

        Directory.CreateDirectory(Path.Combine(outputDirectory, "META-INF"));
        return new ZipExtractionResult(images, warnings, metaInfFound);
    }

    public static async Task CopyMetaInfAsync(
        string zipPath,
        string outputDirectory,
        Action<string>? logLine,
        CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var warnings = new List<string>();
        var found = false;
        foreach (var entry in archive.Entries.Where(e =>
                     e.FullName.Replace('\\', '/').StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase)))
        {
            found = true;
            if (entry.FullName.EndsWith('/'))
            {
                continue;
            }

            await ExtractMetaInfEntryAsync(entry, outputDirectory, warnings, cancellationToken).ConfigureAwait(false);
            logLine?.Invoke($"保留脚本：{entry.FullName}");
        }

        Directory.CreateDirectory(Path.Combine(outputDirectory, "META-INF"));
        if (!found)
        {
            logLine?.Invoke("源 ZIP 中没有 META-INF，已创建空目录");
        }
    }

    private static async Task ExtractMetaInfEntryAsync(
        ZipArchiveEntry entry,
        string outputDirectory,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var normalized = entry.FullName.Replace('\\', '/').TrimStart('/');
        var root = Path.GetFullPath(outputDirectory) + Path.DirectorySeparatorChar;
        var relative = normalized.Replace('/', Path.DirectorySeparatorChar);
        var destination = Path.GetFullPath(Path.Combine(outputDirectory, relative));
        if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add($"已拦截越界路径：{entry.FullName}");
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await using var source = entry.Open();
        await using var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous);
        await source.CopyToAsync(target, 64 * 1024, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsImageEntry(ZipArchiveEntry entry) =>
        entry.FullName.EndsWith(".img", StringComparison.OrdinalIgnoreCase);

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "image" : safe;
    }
}
