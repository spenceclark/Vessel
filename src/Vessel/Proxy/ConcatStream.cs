namespace Vessel.Proxy;

/// <summary>
/// A read-only stream that yields a byte prefix and then the remainder of an inner stream.
/// Used only on the injectStreamUsage over-cap path (D11): the request body was read up to
/// the capture cap to decide whether to inject, found to be larger, and must now be
/// forwarded unmodified — prefix (the bytes already read) then the rest of the original.
/// </summary>
public sealed class ConcatStream(byte[] prefix, Stream rest) : Stream
{
    private int _prefixPosition;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int fromPrefix = ReadPrefix(buffer);
        return fromPrefix > 0 ? fromPrefix : rest.Read(buffer);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int fromPrefix = ReadPrefix(buffer.Span);
        return fromPrefix > 0 ? fromPrefix : await rest.ReadAsync(buffer, cancellationToken);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private int ReadPrefix(Span<byte> buffer)
    {
        int remaining = prefix.Length - _prefixPosition;
        if (remaining <= 0)
        {
            return 0;
        }

        int n = Math.Min(remaining, buffer.Length);
        prefix.AsSpan(_prefixPosition, n).CopyTo(buffer);
        _prefixPosition += n;
        return n;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
