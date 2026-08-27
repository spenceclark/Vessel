namespace Vessel.Capture;

/// <summary>
/// Write-through tee over the response body: every chunk goes to the client first,
/// then to the capture buffer — capture work never delays or withholds a byte.
/// Flushes pass straight through, preserving unbuffered streaming. The first write also
/// checks whether to emit the live <c>first_token</c> SSE event (D5) — a cheap check
/// (content type + subscriber-emptiness) that never touches the client's bytes.
/// </summary>
public sealed class ResponseTeeStream(Stream inner, CaptureContext capture, HttpContext httpContext) : Stream
{
    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => inner.CanWrite;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count) =>
        Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length > 0 && capture.MarkFirstResponseByte())
        {
            capture.EmitFirstTokenIfStreamed(httpContext.Response.ContentType);
        }

        inner.Write(buffer);
        capture.MarkLastResponseByte();
        capture.ResponseBuffer.Append(buffer);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.Length > 0 && capture.MarkFirstResponseByte())
        {
            capture.EmitFirstTokenIfStreamed(httpContext.Response.ContentType);
        }

        await inner.WriteAsync(buffer, cancellationToken);
        capture.MarkLastResponseByte();
        capture.ResponseBuffer.Append(buffer.Span);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush() => inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}
