using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using FlashKit.Core.Abstractions;
using FlashKit.Core.Models;
using FlashKit.Core.Parsing;

namespace FlashKit.Services.Packages;

/// <summary>
/// 首版只实现“纯 img 直塞 ZIP”：
/// - img 放 ZIP 根级，使用 Store；
/// - META-INF/com/google/android/update-binary 与 updater-script 使用 Deflate；
/// - 先在 %TEMP% 组装并校验，再压缩，源 img 所在目录不会被写入任何临时文件。
/// </summary>
public sealed partial class ZipImagesPacker : IPackagePacker
{
    private const int BufferSize = 1024 * 1024;
    private readonly ILoggingService _logging;

    public ZipImagesPacker(ILoggingService logging)
    {
        _logging = logging;
    }

    public PackValidationResult Validate(IReadOnlyList<PackImageInput> images)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        if (images.Count == 0)
        {
            errors.Add("请至少选择一个 .img 镜像。");
            return new PackValidationResult { IsValid = false, Errors = errors, Warnings = warnings };
        }

        var partitions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var image in images)
        {
            if (string.IsNullOrWhiteSpace(image.PartitionName))
            {
                errors.Add($"文件 {Path.GetFileName(image.FilePath)} 未指定分区名。");
            }
            else if (!PartitionNameRegex().IsMatch(image.PartitionName) ||
                     image.PartitionName is "." or ".." ||
                     IsReservedWindowsName(image.PartitionName))
            {
                errors.Add($"分区名 \"{image.PartitionName}\" 含非法字符或为系统保留名。");
            }
            else if (!partitions.Add(image.PartitionName))
            {
                errors.Add($"分区 {image.PartitionName} 重复。");
            }

            if (!File.Exists(image.FilePath))
            {
                errors.Add($"镜像不存在：{image.FilePath}");
                continue;
            }

            if (!Path.GetExtension(image.FilePath).Equals(".img", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"只支持 .img 文件：{Path.GetFileName(image.FilePath)}");
            }

            var fileInfo = new FileInfo(image.FilePath);
            if (fileInfo.Length == 0)
            {
                errors.Add($"镜像为空：{Path.GetFileName(image.FilePath)}");
            }

            var baseName = Path.GetFileNameWithoutExtension(image.FilePath);
            if (!string.IsNullOrWhiteSpace(image.PartitionName) &&
                !baseName.Equals(image.PartitionName, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"文件名 {Path.GetFileName(image.FilePath)} 与分区名 {image.PartitionName} 不一致，将按分区名写入 ZIP。");
            }

            var inferred = PartitionMatcher.InferPartitionName(
                image.FilePath,
                new[] { image.PartitionName });
            if (inferred is null)
            {
                warnings.Add($"无法从文件名推导分区，使用手动指定：{image.FilePath}");
            }
        }

        return new PackValidationResult
        {
            IsValid = errors.Count == 0,
            Errors = errors,
            Warnings = warnings
        };
    }

    public async Task<PackResult> PackAsync(
        IReadOnlyList<PackImageInput> images,
        string outputZipPath,
        IProgress<OperationProgress>? progress = null,
        Action<string>? logLine = null,
        CancellationToken cancellationToken = default)
    {
        var validation = Validate(images);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, validation.Errors));
        }

        var outputFullPath = Path.GetFullPath(outputZipPath);
        if (!Path.GetExtension(outputFullPath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("输出文件必须使用 .zip 扩展名。");
        }

        var outputDirectory = Path.GetDirectoryName(outputFullPath)
            ?? throw new InvalidOperationException("无法确定输出目录。");
        Directory.CreateDirectory(outputDirectory);

        var staging = Path.Combine(Path.GetTempPath(), "FlashKit", "pack-" + Guid.NewGuid().ToString("N"));
        var tempZip = Path.Combine(outputDirectory, ".flashkit-" + Guid.NewGuid().ToString("N") + ".zip.tmp");
        var warnings = new List<string>(validation.Warnings);
        try
        {
            var androidDirectory = Path.Combine(staging, "META-INF", "com", "google", "android");
            Directory.CreateDirectory(androidDirectory);

            var totalBytes = images.Sum(i => new FileInfo(i.FilePath).Length);
            long copied = 0;
            var lastCopyPercent = -1.0;
            foreach (var image in images)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destination = Path.Combine(staging, image.PartitionName + ".img");
                await CopyFileAsync(
                    image.FilePath,
                    destination,
                    bytes =>
                    {
                        copied += bytes;
                        var percent = totalBytes > 0 ? copied * 40.0 / totalBytes : 0;
                        if (percent - lastCopyPercent < 0.2)
                        {
                            return;
                        }

                        lastCopyPercent = percent;
                        progress?.Report(new OperationProgress
                        {
                            Percentage = percent,
                            Status = $"正在组装 {image.PartitionName}.img",
                            BytesProcessed = copied,
                            TotalBytes = totalBytes,
                            CurrentItem = image.PartitionName
                        });
                    },
                    cancellationToken).ConfigureAwait(false);
                logLine?.Invoke($"已加入 {image.PartitionName}.img（{PartitionInfo.FormatBytes(new FileInfo(destination).Length)}）");
            }

            var updateBinary = RenderUpdateBinary(images);
            var updaterScript = RenderUpdaterScript(images);
            await WriteTextAsync(Path.Combine(androidDirectory, "update-binary"), updateBinary, cancellationToken).ConfigureAwait(false);
            await WriteTextAsync(Path.Combine(androidDirectory, "updater-script"), updaterScript, cancellationToken).ConfigureAwait(false);
            logLine?.Invoke("已生成 update-binary 与 updater-script");

            var structure = ValidateStaging(staging, images);
            if (!structure.IsValid)
            {
                throw new InvalidOperationException("打包结构校验失败：" + string.Join("；", structure.Errors));
            }

            await CreateZipAsync(
                staging,
                tempZip,
                images,
                totalBytes,
                progress,
                cancellationToken).ConfigureAwait(false);

            // .NET 的 overwrite 重载会在同一卷上原子替换目标，避免先删除旧包导致中断后两头落空。
            File.Move(tempZip, outputFullPath, overwrite: true);
            var result = new PackResult(
                outputFullPath,
                new FileInfo(outputFullPath).Length,
                images.Count,
                warnings);

            _logging.Info($"打包完成：{outputFullPath}（{images.Count} 个镜像）", "Package");
            progress?.Report(new OperationProgress
            {
                Percentage = 100,
                Status = "打包完成",
                BytesProcessed = result.SizeBytes,
                TotalBytes = result.SizeBytes
            });
            return result;
        }
        catch (Exception ex)
        {
            _logging.Error($"打包失败：{ex.Message}", "Package");
            throw;
        }
        finally
        {
            TryDeleteDirectory(staging);
            try
            {
                if (File.Exists(tempZip))
                {
                    File.Delete(tempZip);
                }
            }
            catch
            {
                // 临时 ZIP 清理失败不应覆盖原始错误。
            }
        }
    }

    private static async Task CreateZipAsync(
        string staging,
        string zipPath,
        IReadOnlyList<PackImageInput> images,
        long totalImageBytes,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        await using var file = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true);

        long zippedBytes = 0;
        var lastZipPercent = -1.0;
        foreach (var image in images)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourcePath = Path.Combine(staging, image.PartitionName + ".img");
            var entry = archive.CreateEntry(image.PartitionName + ".img", CompressionLevel.NoCompression);
            entry.ExternalAttributes = 0x81A4 << 16;
            await using var entryStream = entry.Open();
            await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[BufferSize];
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                await entryStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                zippedBytes += read;
                var percent = totalImageBytes > 0
                    ? 40 + zippedBytes * 50.0 / totalImageBytes
                    : 90;
                if (percent - lastZipPercent < 0.2)
                {
                    continue;
                }

                lastZipPercent = percent;
                progress?.Report(new OperationProgress
                {
                    Percentage = Math.Clamp(percent, 40, 90),
                    Status = $"正在压缩 {image.PartitionName}.img",
                    BytesProcessed = zippedBytes,
                    TotalBytes = totalImageBytes,
                    CurrentItem = image.PartitionName
                });
            }
        }

        // 脚本使用 Deflate，顺序与 recovery 惯例一致。
        foreach (var relative in new[]
                 {
                     "META-INF/com/google/android/update-binary",
                     "META-INF/com/google/android/updater-script"
                 })
        {
            var entry = archive.CreateEntry(relative, CompressionLevel.Optimal);
            entry.ExternalAttributes = (relative.EndsWith("update-binary", StringComparison.Ordinal)
                ? 0x81ED
                : 0x81A4) << 16;
            await using var entryStream = entry.Open();
            await using var source = new FileStream(
                Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar)),
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous);
            await source.CopyToAsync(entryStream, 64 * 1024, cancellationToken).ConfigureAwait(false);
        }

        progress?.Report(new OperationProgress
        {
            Percentage = 95,
            Status = "正在完成 ZIP 结构",
            BytesProcessed = totalImageBytes,
            TotalBytes = totalImageBytes
        });
    }

    private static string RenderUpdateBinary(IReadOnlyList<PackImageInput> images)
    {
        var template = ReadTemplate("update-binary.template");
        var partitions = string.Join(' ', images.Select(i => i.PartitionName));
        return template.Replace("{{PARTITIONS}}", partitions, StringComparison.Ordinal);
    }

    private static string RenderUpdaterScript(IReadOnlyList<PackImageInput> images)
    {
        var template = ReadTemplate("updater-script.template");
        var lines = string.Join(
            Environment.NewLine,
            images.Select(i =>
                $"package_extract_file(\"{i.PartitionName}.img\", \"/dev/block/by-name/{i.PartitionName}\");"));
        var text = template
            .Replace("{{VERSION}}", AppInfo.Version, StringComparison.Ordinal)
            .Replace("{{FLASH_LINES}}", lines, StringComparison.Ordinal);
        return text.Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static string ReadTemplate(string name)
    {
        var assembly = typeof(PackagePacker).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("Templates." + name, StringComparison.OrdinalIgnoreCase));
        if (resourceName is null)
        {
            throw new InvalidOperationException($"内置模板缺失：{name}");
        }

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"无法读取内置模板：{name}");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static PackValidationResult ValidateStaging(string staging, IReadOnlyList<PackImageInput> images)
    {
        var errors = new List<string>();
        var expected = images
            .Select(i => i.PartitionName + ".img")
            .Concat(["META-INF/com/google/android/update-binary", "META-INF/com/google/android/updater-script"])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var actual = Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(staging, f).Replace('\\', '/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var missing in expected.Except(actual))
        {
            errors.Add($"缺少文件 {missing}");
        }

        foreach (var extra in actual.Except(expected))
        {
            errors.Add($"存在多余文件 {extra}");
        }

        return new PackValidationResult
        {
            IsValid = errors.Count == 0,
            Errors = errors
        };
    }

    private static async Task CopyFileAsync(
        string sourcePath,
        string destinationPath,
        Action<int> onBytes,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous);
        var buffer = new byte[BufferSize];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            onBytes(read);
        }
    }

    private static async Task WriteTextAsync(string path, string text, CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken).ConfigureAwait(false);
    }

    private static bool IsReservedWindowsName(string name)
    {
        var baseName = Path.GetFileNameWithoutExtension(name).ToUpperInvariant();
        return baseName is "CON" or "PRN" or "AUX" or "NUL" or
            "COM1" or "COM2" or "COM3" or "COM4" or "COM5" or "COM6" or "COM7" or "COM8" or "COM9" or
            "LPT1" or "LPT2" or "LPT3" or "LPT4" or "LPT5" or "LPT6" or "LPT7" or "LPT8" or "LPT9";
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
            // 忽略临时目录清理失败。
        }
    }

    [GeneratedRegex("^[A-Za-z0-9._-]+$")]
    private static partial Regex PartitionNameRegex();
}


/// <summary>
/// 打包服务门面。首版只注册纯 img ZIP 实现；后续增加 payload 回打包时，
/// 可在这一层按格式分派，UI 与接口保持不变。
/// </summary>
public sealed class PackagePacker : IPackagePacker
{
    private readonly ZipImagesPacker _zipImagesPacker;

    public PackagePacker(ILoggingService logging)
    {
        _zipImagesPacker = new ZipImagesPacker(logging);
    }

    public PackValidationResult Validate(IReadOnlyList<PackImageInput> images) =>
        _zipImagesPacker.Validate(images);

    public Task<PackResult> PackAsync(
        IReadOnlyList<PackImageInput> images,
        string outputZipPath,
        IProgress<OperationProgress>? progress = null,
        Action<string>? logLine = null,
        CancellationToken cancellationToken = default) =>
        _zipImagesPacker.PackAsync(images, outputZipPath, progress, logLine, cancellationToken);
}
