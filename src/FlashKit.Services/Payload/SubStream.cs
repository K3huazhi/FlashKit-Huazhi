namespace FlashKit.Services.Payload;

/// <summary>
/// 将 payload.bin 中某个 operation 的数据段包装成只读流。
/// 这样 XZ/BZip2 解压器可以边读边写，不需要把整个压缩块读入内存。
/// </summary>
internal sealed class SubStream : Stream
{
    private readonly Stream _baseStream;
    private readonly long _start;
    private readonly long _length;
    private readonly bool _leaveOpen;
    private long _position;

    public SubStream(Stream baseStream, long start, long length, bool leaveOpen = true)
    {
        if (!baseStream.CanRead || !baseStream.CanSeek)
        {
            throw new ArgumentException("底层流必须可读且可定位", nameof(baseStream));
        }

        if (start < 0 || length < 0 || start + length > baseStream.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "子流范围超出底层流长度");
        }

        _baseStream = baseStream;
        _start = start;
        _length = length;
        _leaveOpen = leaveOpen;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var remaining = _length - _position;
        if (remaining <= 0 || buffer.Length == 0)
        {
            return 0;
        }

        var toRead = (int)Math.Min(buffer.Length, remaining);
        _baseStream.Position = _start + _position;
        var read = _baseStream.Read(buffer[..toRead]);
        _position += read;
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var remaining = _length - _position;
        if (remaining <= 0 || buffer.Length == 0)
        {
            return 0;
        }

        var toRead = (int)Math.Min(buffer.Length, remaining);
        _baseStream.Position = _start + _position;
        var read = await _baseStream.ReadAsync(buffer[..toRead], cancellationToken).ConfigureAwait(false);
        _position += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };

        if (target < 0 || target > _length)
        {
            throw new IOException("子流定位超出范围");
        }

        _position = target;
        return _position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_leaveOpen)
        {
            _baseStream.Dispose();
        }

        base.Dispose(disposing);
    }
}
