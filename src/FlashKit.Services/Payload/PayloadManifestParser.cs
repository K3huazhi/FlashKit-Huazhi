using System.Buffers.Binary;

namespace FlashKit.Services.Payload;

/// <summary>
/// 极简 protobuf wire-format 读取器，只覆盖 payload manifest 用到的 varint/length-delimited。
/// 这里按 AOSP update_metadata.proto 的字段号手工解析，避免为了一个清单引入完整 protobuf 运行时。
/// 容错策略：未知字段按 wire type 跳过；长度越界、varint 超过 10 字节或 group 类型直接报错。
/// </summary>
internal sealed class ProtoReader
{
    private readonly ReadOnlyMemory<byte> _data;
    private int _position;

    public ProtoReader(ReadOnlyMemory<byte> data)
    {
        _data = data;
    }

    public bool End => _position >= _data.Length;

    public ulong ReadVarint()
    {
        ulong result = 0;
        var shift = 0;
        for (var i = 0; i < 10; i++)
        {
            if (_position >= _data.Length)
            {
                throw new FormatException("protobuf varint 被截断");
            }

            var b = _data.Span[_position++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return result;
            }

            shift += 7;
        }

        throw new FormatException("protobuf varint 超过 10 字节");
    }

    public (int Field, int WireType) ReadTag()
    {
        var tag = ReadVarint();
        var field = (int)(tag >> 3);
        var wire = (int)(tag & 0x07);
        if (field <= 0)
        {
            throw new FormatException("protobuf 字段号无效");
        }

        return (field, wire);
    }

    public ReadOnlyMemory<byte> ReadBytes()
    {
        var length = ReadVarint();
        if (length > int.MaxValue)
        {
            throw new FormatException("protobuf 长度超出范围");
        }

        var len = (int)length;
        if (len < 0 || _position + len > _data.Length)
        {
            throw new FormatException("protobuf 长度字段越界");
        }

        var result = _data.Slice(_position, len);
        _position += len;
        return result;
    }

    public string ReadString()
    {
        var bytes = ReadBytes();
        return System.Text.Encoding.UTF8.GetString(bytes.Span);
    }

    public void Skip(int wireType)
    {
        switch (wireType)
        {
            case 0:
                ReadVarint();
                break;
            case 1:
                Advance(8);
                break;
            case 2:
                var length = ReadVarint();
                if (length > int.MaxValue)
                {
                    throw new FormatException("protobuf 跳过的长度超出范围");
                }

                Advance((int)length);
                break;
            case 5:
                Advance(4);
                break;
            case 3:
            case 4:
                throw new FormatException("不支持 protobuf group 字段");
            default:
                throw new FormatException($"未知 protobuf wire type：{wireType}");
        }
    }

    private void Advance(int count)
    {
        if (count < 0 || _position + count > _data.Length)
        {
            throw new FormatException("protobuf 字段越界");
        }

        _position += count;
    }
}

internal static class PayloadManifestParser
{
    private const int MaxPartitions = 4096;
    private const int MaxOperationsPerPartition = 2_000_000;

    public static PayloadHeader ReadHeader(Stream stream)
    {
        Span<byte> header = stackalloc byte[24];
        try
        {
            stream.ReadExactly(header);
        }
        catch (EndOfStreamException)
        {
            throw new FormatException("payload.bin 头部不足 24 字节");
        }

        if (header[0] != (byte)'C' || header[1] != (byte)'r' || header[2] != (byte)'A' || header[3] != (byte)'U')
        {
            throw new FormatException("不是有效的 payload.bin（缺少 CrAU 魔数）");
        }

        var version = BinaryPrimitives.ReadUInt64BigEndian(header[4..12]);
        var manifestSize = BinaryPrimitives.ReadUInt64BigEndian(header[12..20]);
        var metadataSignatureSize = BinaryPrimitives.ReadUInt32BigEndian(header[20..24]);

        if (version != 2)
        {
            throw new FormatException($"仅支持 payload major version 2，当前为 {version}");
        }

        if (manifestSize == 0 || manifestSize > 256UL * 1024 * 1024)
        {
            throw new FormatException($"payload manifest 大小异常：{manifestSize}");
        }

        var fileLength = stream.Length;
        var dataStart = 24L + (long)manifestSize + metadataSignatureSize;
        if (dataStart < 0 || dataStart > fileLength)
        {
            throw new FormatException("payload 头部声明的 manifest/signature 大小超出文件长度");
        }

        return new PayloadHeader(version, manifestSize, metadataSignatureSize, dataStart, fileLength);
    }

    public static PayloadManifestInfo ParseManifest(ReadOnlyMemory<byte> manifest)
    {
        var reader = new ProtoReader(manifest);
        var result = new PayloadManifestInfo();

        while (!reader.End)
        {
            var (field, wire) = reader.ReadTag();
            switch (field)
            {
                case 3 when wire == 0:
                    var blockSize = reader.ReadVarint();
                    if (blockSize is > 0 and <= 1024 * 1024)
                    {
                        result.BlockSize = (uint)blockSize;
                    }
                    break;
                case 12 when wire == 0:
                    result.MinorVersion = (uint)Math.Min(reader.ReadVarint(), uint.MaxValue);
                    break;
                case 13 when wire == 2:
                    if (result.Partitions.Count >= MaxPartitions)
                    {
                        throw new FormatException("payload 分区数量异常");
                    }

                    result.Partitions.Add(ParsePartition(reader.ReadBytes()));
                    break;
                case 16 when wire == 0:
                    result.PartialUpdate = reader.ReadVarint() != 0;
                    break;
                default:
                    reader.Skip(wire);
                    break;
            }
        }

        return result;
    }

    private static PayloadPartitionUpdate ParsePartition(ReadOnlyMemory<byte> data)
    {
        var reader = new ProtoReader(data);
        var partition = new PayloadPartitionUpdate();
        while (!reader.End)
        {
            var (field, wire) = reader.ReadTag();
            switch (field)
            {
                case 1 when wire == 2:
                    partition.Name = reader.ReadString().Trim();
                    break;
                case 7 when wire == 2:
                    ParsePartitionInfo(reader.ReadBytes(), partition);
                    break;
                case 8 when wire == 2:
                    if (partition.Operations.Count >= MaxOperationsPerPartition)
                    {
                        throw new FormatException($"分区 {partition.Name} 的操作数量异常");
                    }

                    partition.Operations.Add(ParseOperation(reader.ReadBytes()));
                    break;
                default:
                    reader.Skip(wire);
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(partition.Name))
        {
            throw new FormatException("payload 分区名称为空");
        }

        return partition;
    }

    private static void ParsePartitionInfo(ReadOnlyMemory<byte> data, PayloadPartitionUpdate partition)
    {
        var reader = new ProtoReader(data);
        while (!reader.End)
        {
            var (field, wire) = reader.ReadTag();
            switch (field)
            {
                case 1 when wire == 0:
                    partition.NewSize = reader.ReadVarint();
                    break;
                case 2 when wire == 2:
                    partition.NewHash = reader.ReadBytes().ToArray();
                    break;
                default:
                    reader.Skip(wire);
                    break;
            }
        }
    }

    private static PayloadInstallOperation ParseOperation(ReadOnlyMemory<byte> data)
    {
        var reader = new ProtoReader(data);
        var operation = new PayloadInstallOperation();
        while (!reader.End)
        {
            var (field, wire) = reader.ReadTag();
            switch (field)
            {
                case 1 when wire == 0:
                    var typeValue = reader.ReadVarint();
                    operation.Type = Enum.IsDefined(typeof(PayloadOperationType), (int)typeValue)
                        ? (PayloadOperationType)(int)typeValue
                        : PayloadOperationType.Unknown;
                    break;
                case 2 when wire == 0:
                    operation.DataOffset = reader.ReadVarint();
                    break;
                case 3 when wire == 0:
                    operation.DataLength = reader.ReadVarint();
                    break;
                case 6 when wire == 2:
                    operation.DestinationExtents.Add(ParseExtent(reader.ReadBytes()));
                    break;
                default:
                    reader.Skip(wire);
                    break;
            }
        }

        return operation;
    }

    private static PayloadExtent ParseExtent(ReadOnlyMemory<byte> data)
    {
        var reader = new ProtoReader(data);
        ulong start = 0;
        ulong blocks = 0;
        while (!reader.End)
        {
            var (field, wire) = reader.ReadTag();
            switch (field)
            {
                case 1 when wire == 0:
                    start = reader.ReadVarint();
                    break;
                case 2 when wire == 0:
                    blocks = reader.ReadVarint();
                    break;
                default:
                    reader.Skip(wire);
                    break;
            }
        }

        return new PayloadExtent(start, blocks);
    }
}
