using System.Buffers;
using SharpCompress.Compressors;
using FlashKit.Core.Models;
using SharpCompress.Compressors.BZip2;
using SharpCompress.Compressors.Xz;

namespace FlashKit.Services.Payload;

internal sealed record PayloadExtractionResult(
    IReadOnlyList<ManifestImageEntry> Images,
    IReadOnlyList<string> Warnings,
    PayloadHeader Header,
    PayloadManifestInfo Manifest);

/// <summary>
/// A/B payload.bin 解包器。只支持全量 payload 的 REPLACE / REPLACE_BZ / REPLACE_XZ / ZERO / DISCARD。
/// 增量 OTA 需要源包中的旧分区，无法独立还原，因此明确抛 IncrementalOtaException。
/// 全程使用流式解压和定位写入，不把整包或整个分区读入内存。
/// </summary>
internal sealed class PayloadExtractor
{
    private const int CopyBufferSize = 1024 * 1024;

    public async Task<PayloadExtractionResult> ExtractAsync(
        string payloadPath,
        string imagesDirectory,
        IProgress<OperationProgress>? progress = null,
        Action<string>? logLine = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(imagesDirectory);
        await using var payload = new FileStream(
            payloadPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.RandomAccess);

        var header = PayloadManifestParser.ReadHeader(payload);
        if (header.ManifestSize > int.MaxValue)
        {
            throw new PackageFormatException("payload manifest 过大，无法解析");
        }

        var manifestBytes = new byte[(int)header.ManifestSize];
        await payload.ReadExactlyAsync(manifestBytes, cancellationToken).ConfigureAwait(false);

        var manifest = PayloadManifestParser.ParseManifest(manifestBytes);
        if (manifest.Partitions.Count == 0)
        {
            throw new PackageFormatException("payload manifest 中没有分区");
        }

        var warnings = new List<string>();
        ValidateIncremental(manifest, warnings);

        // 先做全量结构校验，再创建输出文件，避免写到一半才发现越界。
        var plans = BuildPlans(manifest, header, warnings);
        EnsureSufficientSpace(imagesDirectory, plans.Sum(p => p.SizeBytes));

        var totalBytes = plans.Sum(p => p.TotalOperationBytes);
        long processedBytes = 0;
        var lastReported = 0.0;
        var images = new List<ManifestImageEntry>();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void ReportProgress(string status, bool force = false)
        {
            var percent = totalBytes > 0 ? Math.Clamp(processedBytes * 100.0 / totalBytes, 0, 100) : 0;
            if (!force && percent - lastReported < 0.5)
            {
                return;
            }

            lastReported = percent;
            progress?.Report(new OperationProgress
            {
                Percentage = percent,
                Status = status,
                BytesProcessed = processedBytes,
                TotalBytes = totalBytes
            });
        }

        progress?.Report(OperationProgress.Indeterminate("解析 payload 清单完成"));
        logLine?.Invoke($"payload 版本 2，块大小 {manifest.BlockSize}，分区 {manifest.Partitions.Count} 个");

        foreach (var plan in plans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileName = UniqueFileName(plan.Partition.Name, usedNames);
            var outputPath = Path.Combine(imagesDirectory, fileName);
            var operationNames = plan.Partition.Operations
                .Select(o => OperationName(o.Type))
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            logLine?.Invoke($"正在解包 {plan.Partition.Name}（{string.Join(", ", operationNames)}）…");
            ReportProgress($"正在解包 {plan.Partition.Name}", force: true);

            await using (var output = new FileStream(
                outputPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                CopyBufferSize,
                FileOptions.Asynchronous | FileOptions.RandomAccess))
            {
                output.SetLength(plan.SizeBytes);

                foreach (var operation in plan.Partition.Operations)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var expectedBytes = ExtentsBytes(operation, manifest.BlockSize);
                    switch (operation.Type)
                    {
                        case PayloadOperationType.Replace:
                            await using (var source = new SubStream(
                                payload,
                                header.DataStart + (long)operation.DataOffset,
                                (long)operation.DataLength,
                                leaveOpen: true))
                            {
                                if ((long)operation.DataLength < expectedBytes)
                                {
                                    throw new PackageFormatException(
                                        $"分区 {plan.Partition.Name} 的 REPLACE 数据不足：" +
                                        $"数据 {operation.DataLength} 字节，目标 {expectedBytes} 字节");
                                }

                                var written = await CopyToExtentsAsync(
                                    source,
                                    output,
                                    operation.DestinationExtents,
                                    manifest.BlockSize,
                                    expectedBytes,
                                    onBytes: null,
                                    cancellationToken).ConfigureAwait(false);
                                processedBytes += written;
                            }
                            break;

                        case PayloadOperationType.ReplaceBz:
                            await using (var compressed = new SubStream(
                                payload,
                                header.DataStart + (long)operation.DataOffset,
                                (long)operation.DataLength,
                                leaveOpen: true))
                            await using (var bzip = new BZip2Stream(compressed, SharpCompress.Compressors.CompressionMode.Decompress, decompressConcatenated: false))
                            {
                                var written = await CopyToExtentsAsync(
                                    bzip,
                                    output,
                                    operation.DestinationExtents,
                                    manifest.BlockSize,
                                    expectedBytes,
                                    onBytes: null,
                                    cancellationToken).ConfigureAwait(false);
                                processedBytes += written;
                            }
                            break;

                        case PayloadOperationType.ReplaceXz:
                            await using (var compressed = new SubStream(
                                payload,
                                header.DataStart + (long)operation.DataOffset,
                                (long)operation.DataLength,
                                leaveOpen: true))
                            await using (var xz = new XZStream(compressed))
                            {
                                var written = await CopyToExtentsAsync(
                                    xz,
                                    output,
                                    operation.DestinationExtents,
                                    manifest.BlockSize,
                                    expectedBytes,
                                    onBytes: null,
                                    cancellationToken).ConfigureAwait(false);
                                processedBytes += written;
                            }
                            break;

                        case PayloadOperationType.Zero:
                        case PayloadOperationType.Discard:
                            var zeroed = await WriteZerosAsync(
                                output,
                                operation.DestinationExtents,
                                manifest.BlockSize,
                                onBytes: null,
                                cancellationToken).ConfigureAwait(false);
                            processedBytes += zeroed;
                            break;

                        default:
                            throw new PackageFormatException(
                                $"分区 {plan.Partition.Name} 含暂不支持的安装操作 {OperationName(operation.Type)}。" +
                                "请使用全量 OTA 或等待后续版本支持。");
                    }

                    processedBytes = Math.Min(processedBytes, totalBytes);
                    ReportProgress($"正在解包 {plan.Partition.Name}");
                }
            }

            if (plan.Partition.NewHash is { Length: 32 } expectedHash)
            {
                ReportProgress($"校验 {plan.Partition.Name}", force: true);
                var actualHash = await ComputeSha256Async(outputPath, cancellationToken).ConfigureAwait(false);
                if (!actualHash.AsSpan().SequenceEqual(expectedHash))
                {
                    throw new PackageFormatException(
                        $"分区 {plan.Partition.Name} 解包结果 SHA256 与 manifest 不一致，已停止输出。");
                }
            }

            var imageInfo = new ManifestImageEntry
            {
                PartitionName = plan.Partition.Name,
                FileName = fileName,
                SizeBytes = new FileInfo(outputPath).Length,
                SourceOperations = operationNames,
                Sha256 = plan.Partition.NewHashHex
            };
            images.Add(imageInfo);
            ReportProgress($"已解包 {plan.Partition.Name}", force: true);
            logLine?.Invoke($"已输出 {fileName}（{PartitionInfo.FormatBytes(imageInfo.SizeBytes)}）");
        }

        progress?.Report(new OperationProgress
        {
            Percentage = 100,
            Status = "payload 解包完成",
            BytesProcessed = totalBytes,
            TotalBytes = totalBytes
        });

        return new PayloadExtractionResult(images, warnings, header, manifest);
    }

    private static void ValidateIncremental(PayloadManifestInfo manifest, List<string> warnings)
    {
        var incrementalTypes = manifest.Partitions
            .SelectMany(p => p.Operations)
            .Select(o => o.Type)
            .Where(IsIncrementalOperation)
            .Distinct()
            .Select(OperationName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        // 只有真正依赖源包旧分区的操作才无法独立解包。
        // 部分厂商的“全量包”也会把 minor_version 设为非 0、partial_update 设为 true，
        // 但操作仍然是 REPLACE/REPLACE_BZ/REPLACE_XZ/ZERO，这些数据是自包含的。
        if (incrementalTypes.Length > 0)
        {
            var detail = string.Join(", ", incrementalTypes);
            throw new IncrementalOtaException(
                $"检测到依赖源包的增量操作（{detail}）。" +
                "增量包必须依赖源包中的旧分区数据，无法独立解出完整 img。" +
                "请改用对应机型的全量 OTA 包。");
        }

        if (manifest.MinorVersion != 0 || manifest.PartialUpdate)
        {
            warnings.Add(
                $"payload 标记为增量/部分更新（minor_version={manifest.MinorVersion}，partial_update={manifest.PartialUpdate}），" +
                "但所有操作均为自包含 REPLACE 数据。已解出包内分区；包内未包含的分区仍需源包。");
        }
    }

    private static bool IsIncrementalOperation(PayloadOperationType type) => type is
        PayloadOperationType.Move or
        PayloadOperationType.Bsdiff or
        PayloadOperationType.SourceCopy or
        PayloadOperationType.SourceBsdiff or
        PayloadOperationType.Puffdiff or
        PayloadOperationType.BrotliBsdiff or
        PayloadOperationType.Zucchini or
        PayloadOperationType.Lz4diffBsdiff or
        PayloadOperationType.Lz4diffPuffdiff;

    private static List<PartitionPlan> BuildPlans(
        PayloadManifestInfo manifest,
        PayloadHeader header,
        List<string> warnings)
    {
        var blockSize = manifest.BlockSize;
        if (blockSize == 0)
        {
            throw new PackageFormatException("payload block_size 为 0");
        }

        var plans = new List<PartitionPlan>(manifest.Partitions.Count);
        foreach (var partition in manifest.Partitions)
        {
            if (partition.Operations.Count == 0)
            {
                warnings.Add($"分区 {partition.Name} 没有任何安装操作，已跳过");
                continue;
            }

            ulong maxEndBlock = 0;
            long totalOperationBytes = 0;
            foreach (var operation in partition.Operations)
            {
                if (operation.Type is PayloadOperationType.Unknown or PayloadOperationType.ReplaceZstd)
                {
                    throw new PackageFormatException(
                        $"分区 {partition.Name} 使用暂不支持的安装操作 {OperationName(operation.Type)}");
                }

                if (operation.Type is not (PayloadOperationType.Replace or PayloadOperationType.ReplaceBz or
                    PayloadOperationType.ReplaceXz or PayloadOperationType.Zero or PayloadOperationType.Discard))
                {
                    throw new PackageFormatException(
                        $"分区 {partition.Name} 使用增量操作 {OperationName(operation.Type)}，无法独立解包");
                }

                if (operation.DestinationExtents.Count == 0)
                {
                    throw new PackageFormatException($"分区 {partition.Name} 的操作缺少目标 extents");
                }

                if (operation.DataLength > 0)
                {
                    var dataStart = checked((ulong)header.DataStart + operation.DataOffset);
                    var end = checked(dataStart + operation.DataLength);
                    if (end < dataStart || end > (ulong)header.FileLength)
                    {
                        throw new PackageFormatException(
                            $"分区 {partition.Name} 的数据段超出 payload 文件末尾");
                    }
                }

                long opBytes = 0;
                foreach (var extent in operation.DestinationExtents)
                {
                    var endBlock = extent.StartBlock + extent.NumBlocks;
                    if (endBlock < extent.StartBlock)
                    {
                        throw new PackageFormatException($"分区 {partition.Name} 的 extent 溢出");
                    }

                    maxEndBlock = Math.Max(maxEndBlock, endBlock);
                    try
                    {
                        opBytes = checked(opBytes + checked((long)(extent.NumBlocks * blockSize)));
                    }
                    catch (OverflowException)
                    {
                        throw new PackageFormatException($"分区 {partition.Name} 的 extents 大小溢出");
                    }
                }

                totalOperationBytes = checked(totalOperationBytes + opBytes);
            }

            long size;
            try
            {
                var extentSize = checked((long)(maxEndBlock * blockSize));
                // new_partition_info.size 是完整分区大小，可能大于最后一个 dst extent 的末尾。
                if (partition.NewSize is { } declaredSize)
                {
                    if (declaredSize > long.MaxValue)
                    {
                        throw new PackageFormatException($"分区 {partition.Name} 声明的大小超出范围");
                    }

                    if ((long)declaredSize < extentSize)
                    {
                        throw new PackageFormatException(
                            $"分区 {partition.Name} 的声明大小小于操作 extents 覆盖范围");
                    }

                    size = (long)declaredSize;
                }
                else
                {
                    size = extentSize;
                }
            }
            catch (OverflowException)
            {
                throw new PackageFormatException($"分区 {partition.Name} 的大小溢出");
            }

            if (size <= 0)
            {
                warnings.Add($"分区 {partition.Name} 的目标大小无效，已跳过");
                continue;
            }

            plans.Add(new PartitionPlan(partition, size, totalOperationBytes));
        }

        if (plans.Count == 0)
        {
            throw new PackageFormatException("payload 中没有可解包的分区");
        }

        return plans;
    }

    private static void EnsureSufficientSpace(string imagesDirectory, long requiredBytes)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(imagesDirectory));
            if (string.IsNullOrWhiteSpace(root))
            {
                return;
            }

            var drive = new DriveInfo(root);
            const long reserve = 512L * 1024 * 1024;
            if (drive.AvailableFreeSpace < requiredBytes + reserve)
            {
                throw new PackageFormatException(
                    $"输出磁盘空间不足：镜像约需 {PartitionInfo.FormatBytes(requiredBytes)}，" +
                    $"可用 {PartitionInfo.FormatBytes(drive.AvailableFreeSpace)}。");
            }
        }
        catch (PackageFormatException)
        {
            throw;
        }
        catch
        {
            // 无法读取磁盘信息时不阻断解包，实际写入会给出 IO 错误。
        }
    }

    private static async Task<long> CopyToExtentsAsync(
        Stream source,
        FileStream output,
        IReadOnlyList<PayloadExtent> extents,
        uint blockSize,
        long expectedBytes,
        Action<long>? onBytes,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        long written = 0;
        try
        {
            foreach (var extent in extents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var offset = checked((long)(extent.StartBlock * blockSize));
                var extentLength = checked((long)(extent.NumBlocks * blockSize));
                output.Position = offset;
                var remaining = extentLength;

                while (remaining > 0)
                {
                    var toRead = (int)Math.Min(buffer.Length, remaining);
                    var read = await source.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken).ConfigureAwait(false);
                    if (read <= 0)
                    {
                        throw new PackageFormatException(
                            $"解压/读取数据不足：已写入 {written} 字节，目标至少 {expectedBytes} 字节");
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    remaining -= read;
                    written += read;
                    onBytes?.Invoke(read);
                }
            }

            if (written != expectedBytes)
            {
                throw new PackageFormatException($"写入长度异常：{written} / {expectedBytes} 字节");
            }

            // 检查解压流是否还有多余数据。多余数据不会破坏镜像，但值得提示。
            var extra = await source.ReadAsync(buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
            if (extra > 0)
            {
                // 由调用方决定是否写入警告；这里不抛异常，避免个别 OTA 的尾部填充导致解包失败。
            }

            return written;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<byte[]> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
        return hash;
    }

    private static async Task<long> WriteZerosAsync(
        FileStream output,
        IReadOnlyList<PayloadExtent> extents,
        uint blockSize,
        Action<long>? onBytes,
        CancellationToken cancellationToken)
    {
        var zeros = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        Array.Clear(zeros);
        long written = 0;
        try
        {
            foreach (var extent in extents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                output.Position = checked((long)(extent.StartBlock * blockSize));
                var remaining = checked((long)(extent.NumBlocks * blockSize));
                while (remaining > 0)
                {
                    var count = (int)Math.Min(zeros.Length, remaining);
                    await output.WriteAsync(zeros.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                    remaining -= count;
                    written += count;
                    onBytes?.Invoke(count);
                }
            }

            return written;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(zeros);
        }
    }

    private static long ExtentsBytes(PayloadInstallOperation operation, uint blockSize)
    {
        long total = 0;
        foreach (var extent in operation.DestinationExtents)
        {
            total = checked(total + checked((long)(extent.NumBlocks * blockSize)));
        }

        return total;
    }

    private static string UniqueFileName(string partitionName, HashSet<string> used)
    {
        var safe = SanitizeName(partitionName);
        var candidate = safe + ".img";
        var index = 1;
        while (!used.Add(candidate))
        {
            candidate = $"{safe}_{index++}.img";
        }

        return candidate;
    }

    private static string SanitizeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Trim().Select(c => invalid.Contains(c) || c == '.' && name == "." ? '_' : c).ToArray();
        var safe = new string(chars);
        if (string.IsNullOrWhiteSpace(safe) || safe is "." or "..")
        {
            return "partition";
        }

        return safe;
    }

    private static string OperationName(PayloadOperationType type) => type switch
    {
        PayloadOperationType.Replace => "REPLACE",
        PayloadOperationType.ReplaceBz => "REPLACE_BZ",
        PayloadOperationType.Move => "MOVE",
        PayloadOperationType.Bsdiff => "BSDIFF",
        PayloadOperationType.SourceCopy => "SOURCE_COPY",
        PayloadOperationType.SourceBsdiff => "SOURCE_BSDIFF",
        PayloadOperationType.Zero => "ZERO",
        PayloadOperationType.Discard => "DISCARD",
        PayloadOperationType.ReplaceXz => "REPLACE_XZ",
        PayloadOperationType.Puffdiff => "PUFFDIFF",
        PayloadOperationType.BrotliBsdiff => "BROTLI_BSDIFF",
        PayloadOperationType.Zucchini => "ZUCCHINI",
        PayloadOperationType.Lz4diffBsdiff => "LZ4DIFF_BSDIFF",
        PayloadOperationType.Lz4diffPuffdiff => "LZ4DIFF_PUFFDIFF",
        PayloadOperationType.ReplaceZstd => "REPLACE_ZSTD",
        _ => "UNKNOWN"
    };

    private sealed record PartitionPlan(
        PayloadPartitionUpdate Partition,
        long SizeBytes,
        long TotalOperationBytes);
}
