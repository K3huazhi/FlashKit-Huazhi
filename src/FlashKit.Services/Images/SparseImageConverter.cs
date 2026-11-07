using FlashKit.Core.Models;

namespace FlashKit.Services.Images;

/// <summary>
/// Android sparse ↔ raw 流式转换。支持 RAW / FILL / DON'T CARE / CRC32 chunk；
/// 不使用整包内存，输出先写 .tmp，成功后再替换目标。
/// </summary>
public sealed class SparseImageConverter
{
    private const uint SparseMagic = 0xED26FF3A;
    private const ushort RawChunk = 0xCAC1;
    private const ushort FillChunk = 0xCAC2;
    private const ushort DontCareChunk = 0xCAC3;
    private const ushort CrcChunk = 0xCAC4;
    private const uint DefaultBlockSize = 4096;

    public async Task ConvertToRawAsync(
        string sourcePath,
        string outputPath,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var header = ReadHeader(source);
        await using var output = CreateTemp(outputPath, out var tempPath);
        try
        {
            var totalBytes = (long)header.BlockSize * header.TotalBlocks;
            long written = 0;
            var lastPercent = -1.0;
            var crc = new Crc32();
            for (var i = 0u; i < header.TotalChunks; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var chunkHeader = new byte[12];
                await source.ReadExactlyAsync(chunkHeader, cancellationToken).ConfigureAwait(false);
                var type = BitConverter.ToUInt16(chunkHeader, 0);
                var chunkBlocks = BitConverter.ToUInt32(chunkHeader, 4);
                var totalSize = BitConverter.ToUInt32(chunkHeader, 8);
                var dataSize = totalSize >= 12 ? totalSize - 12 : 0;
                var bytes = (long)chunkBlocks * header.BlockSize;
                switch (type)
                {
                    case RawChunk:
                        if (dataSize != bytes)
                        {
                            throw new InvalidDataException($"sparse RAW chunk 大小不一致：chunk={chunkBlocks} blocks, data={dataSize}");
                        }

                        await CopyAsync(source, output, dataSize, crc, cancellationToken).ConfigureAwait(false);
                        written += bytes;
                        break;
                    case FillChunk:
                    {
                        var fill = new byte[4];
                        await source.ReadExactlyAsync(fill, cancellationToken).ConfigureAwait(false);
                        if (dataSize != 4)
                        {
                            throw new InvalidDataException("sparse FILL chunk 数据不是 4 字节");
                        }

                        var block = new byte[header.BlockSize];
                        for (var b = 0; b < (int)header.BlockSize; b += 4)
                        {
                            fill.CopyTo(block, b);
                        }

                        for (var b = 0u; b < chunkBlocks; b++)
                        {
                            await output.WriteAsync(block, cancellationToken).ConfigureAwait(false);
                            crc.Append(block);
                        }

                        written += bytes;
                        break;
                    }
                    case DontCareChunk:
                    {
                        var zero = new byte[1024 * 1024];
                        var remaining = bytes;
                        while (remaining > 0)
                        {
                            var count = (int)Math.Min(zero.Length, remaining);
                            await output.WriteAsync(zero.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                            crc.Append(zero.AsSpan(0, count));
                            remaining -= count;
                        }

                        written += bytes;
                        break;
                    }
                    case CrcChunk:
                    {
                        var crcBytes = new byte[dataSize];
                        await source.ReadExactlyAsync(crcBytes, cancellationToken).ConfigureAwait(false);
                        if (crcBytes.Length != 4)
                        {
                            throw new InvalidDataException("sparse CRC chunk 不是 4 字节");
                        }

                        break;
                    }
                    default:
                        throw new InvalidDataException($"未知 sparse chunk 类型 0x{type:X4}");
                }

                var percent = totalBytes > 0 ? written * 100.0 / totalBytes : 0;
                if (percent - lastPercent >= 0.5 || i + 1 == header.TotalChunks)
                {
                    lastPercent = percent;
                    progress?.Report(new OperationProgress
                    {
                        Percentage = Math.Clamp(percent, 0, 100),
                        Status = "正在转换 sparse → raw",
                        BytesProcessed = written,
                        TotalBytes = totalBytes
                    });
                }
            }

            if (written != totalBytes)
            {
                throw new InvalidDataException($"sparse 展开大小不匹配：{written} / {totalBytes}");
            }

            if (header.Checksum != 0 && crc.Value != header.Checksum)
            {
                throw new InvalidDataException(
                    $"sparse CRC32 校验失败：header=0x{header.Checksum:X8}, actual=0x{crc.Value:X8}");
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Close();
            File.Move(tempPath, outputPath, overwrite: true);
            progress?.Report(new OperationProgress { Percentage = 100, Status = "转换完成" });
        }
        catch
        {
            output.Close();
            TryDelete(tempPath);
            throw;
        }
    }

    public async Task ConvertToSparseAsync(
        string sourcePath,
        string outputPath,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var length = source.Length;
        var totalBlocks = (uint)((length + DefaultBlockSize - 1) / DefaultBlockSize);
        const uint chunkBlocks = 1024;
        var totalChunks = (uint)((totalBlocks + chunkBlocks - 1) / chunkBlocks);
        await using var output = CreateTemp(outputPath, out var tempPath);
        try
        {
            var header = new byte[28];
            WriteUInt32(header, 0, SparseMagic);
            WriteUInt16(header, 4, 1);
            WriteUInt16(header, 6, 0);
            WriteUInt16(header, 8, 28);
            WriteUInt16(header, 10, 12);
            WriteUInt32(header, 12, DefaultBlockSize);
            WriteUInt32(header, 16, totalBlocks);
            WriteUInt32(header, 20, totalChunks);
            WriteUInt32(header, 24, 0);
            await output.WriteAsync(header, cancellationToken).ConfigureAwait(false);

            var buffer = new byte[DefaultBlockSize * chunkBlocks];
            long processed = 0;
            for (var index = 0u; index < totalChunks; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var blocks = Math.Min(chunkBlocks, totalBlocks - index * chunkBlocks);
                var expected = (int)(blocks * DefaultBlockSize);
                var read = 0;
                while (read < expected)
                {
                    var n = await source.ReadAsync(buffer.AsMemory(read, expected - read), cancellationToken).ConfigureAwait(false);
                    if (n == 0)
                    {
                        break;
                    }

                    read += n;
                }

                if (read < expected)
                {
                    Array.Clear(buffer, read, expected - read);
                }

                var chunkHeader = new byte[12];
                WriteUInt16(chunkHeader, 0, RawChunk);
                WriteUInt16(chunkHeader, 2, 0);
                WriteUInt32(chunkHeader, 4, blocks);
                WriteUInt32(chunkHeader, 8, (uint)(12 + expected));
                await output.WriteAsync(chunkHeader, cancellationToken).ConfigureAwait(false);
                await output.WriteAsync(buffer.AsMemory(0, expected), cancellationToken).ConfigureAwait(false);
                processed += blocks * DefaultBlockSize;
                var percent = length > 0 ? Math.Min(100, processed * 100.0 / length) : 100;
                if (index % 8 == 0 || index + 1 == totalChunks)
                {
                    progress?.Report(new OperationProgress
                    {
                        Percentage = percent,
                        Status = "正在转换 raw → sparse",
                        BytesProcessed = processed,
                        TotalBytes = length
                    });
                }
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Close();
            File.Move(tempPath, outputPath, overwrite: true);
            progress?.Report(new OperationProgress { Percentage = 100, Status = "转换完成" });
        }
        catch
        {
            output.Close();
            TryDelete(tempPath);
            throw;
        }
    }

    private static (uint BlockSize, uint TotalBlocks, uint TotalChunks, uint Checksum) ReadHeader(FileStream stream)
    {
        var header = new byte[28];
        stream.ReadExactly(header);
        if (BitConverter.ToUInt32(header, 0) != SparseMagic)
        {
            throw new InvalidDataException("不是 Android sparse 镜像");
        }

        var blockSize = BitConverter.ToUInt32(header, 12);
        var totalBlocks = BitConverter.ToUInt32(header, 16);
        var totalChunks = BitConverter.ToUInt32(header, 20);
        var checksum = BitConverter.ToUInt32(header, 24);
        if (blockSize == 0 || totalBlocks == 0)
        {
            throw new InvalidDataException("sparse header 的块大小/块数为 0");
        }

        return (blockSize, totalBlocks, totalChunks, checksum);
    }

    private static FileStream CreateTemp(string outputPath, out string tempPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        tempPath = outputPath + ".tmp";
        if (File.Exists(tempPath))
        {
            File.Delete(tempPath);
        }

        return new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous);
    }

    private static async Task CopyAsync(Stream source, Stream output, long count, Crc32 crc, CancellationToken ct)
    {
        var buffer = new byte[(int)Math.Min(count, 1024 * 1024)];
        var remaining = count;
        while (remaining > 0)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("sparse RAW chunk 提前结束");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            crc.Append(buffer.AsSpan(0, read));
            remaining -= read;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static void WriteUInt16(byte[] buffer, int offset, ushort value) =>
        BitConverter.GetBytes(value).CopyTo(buffer, offset);

    private static void WriteUInt32(byte[] buffer, int offset, uint value) =>
        BitConverter.GetBytes(value).CopyTo(buffer, offset);

    private sealed class Crc32
    {
        private static readonly uint[] Table = BuildTable();
        private uint _value = 0xFFFFFFFF;

        public uint Value => ~_value;

        public void Append(ReadOnlySpan<byte> data)
        {
            var value = _value;
            foreach (var b in data)
            {
                value = Table[(value ^ b) & 0xFF] ^ (value >> 8);
            }

            _value = value;
        }

        private static uint[] BuildTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                var c = i;
                for (var k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                }

                table[i] = c;
            }

            return table;
        }
    }
}
