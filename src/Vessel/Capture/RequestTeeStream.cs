namespace Vessel.Capture;

/// <summary>
/// Read-through tee over the request body: bytes are appended to the capture buffer as
/// the forwarder reads them upstream; read results are never altered. The last read
/// (data or EOF) stamps the "request fully forwarded" mark used as the TTFT baseline,
/// and a genuine EOF (a non-zero-length request that came back empty — YARP also issues
/// zero-length probe reads, which are not EOF) kicks off the <c>request_ready</c> model
/// parse (D5).
/// </summary>
public sealed class RequestTeeStream(Stream inner, CaptureContext capture) : Stream
{
    public override bool CanRead => inner.CanRead;

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
        int read = inner.Read(buffer);
        Observe(buffer[..Math.Max(read, 0)], buffer.Length);
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int read = await inner.ReadAsync(buffer, cancellationToken);
        Observe(buffer.Span[..Math.Max(read, 0)], buffer.Length);
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private void Observe(ReadOnlySpan<byte> data, int requestedLength)
    {
        if (data.Length > 0)
        {
            capture.RequestBuffer.Append(data);
        }

        capture.MarkRequestForwarded();

        // A 0-byte result only means EOF when the caller actually asked for data; YARP
        // issues zero-length probe reads (requestedLength 0) that trivially return 0
        // without the stream being anywhere near exhausted.
        if (data.Length == 0 && requestedLength > 0)
        {
            capture.EmitRequestReadyIfParseable();
        }
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
