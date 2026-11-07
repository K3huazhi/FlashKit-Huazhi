namespace FlashKit.Core.Models;

public sealed record SparseImageInfo(
    bool IsSparse,
    uint MajorVersion,
    uint MinorVersion,
    uint BlockSize,
    uint TotalBlocks,
    uint TotalChunks)
{
    public long? ExpandedSizeBytes =>
        IsSparse && BlockSize > 0 && TotalBlocks > 0
            ? (long)BlockSize * TotalBlocks
            : null;
}

public static class ImageFormats
{
    // Android sparse image magic: 0x3AFF26ED，小端存储为 ED 26 FF 3A。
    private static readonly byte[] SparseMagic = [0x3A, 0xFF, 0x26, 0xED];

    /// <summary>
    /// 只读取 28 字节头部，不加载镜像。解析失败时返回 IsSparse=false，
    /// 让调用方按普通 raw 镜像继续处理；真正损坏的 sparse 会在 fastboot 阶段报错。
    /// </summary>
    public static SparseImageInfo Inspect(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> header = stackalloc byte[28];
            try
            {
                stream.ReadExactly(header);
            }
            catch (EndOfStreamException)
            {
                return new SparseImageInfo(false, 0, 0, 0, 0, 0);
            }

            if (!header[..4].SequenceEqual(SparseMagic))
            {
                return new SparseImageInfo(false, 0, 0, 0, 0, 0);
            }

            var major = BitConverter.ToUInt16(header[4..6]);
            var minor = BitConverter.ToUInt16(header[6..8]);
            var blockSize = BitConverter.ToUInt32(header[12..16]);
            var totalBlocks = BitConverter.ToUInt32(header[16..20]);
            var totalChunks = BitConverter.ToUInt32(header[20..24]);

            return new SparseImageInfo(true, major, minor, blockSize, totalBlocks, totalChunks);
        }
        catch
        {
            return new SparseImageInfo(false, 0, 0, 0, 0, 0);
        }
    }
}
