using System.Threading.Channels;

namespace Vessel.Capture;

/// <summary>
/// The unbounded queue between the request path and the background writer. Enqueue is
/// fire-and-forget from the request's point of view; the writer is the single reader.
/// Carries the <see cref="CaptureWork"/> union — captured requests and, off the request
/// path, writer-thread control commands (D4).
/// </summary>
public sealed class CaptureChannel
{
    private readonly Channel<CaptureWork> _channel = Channel.CreateUnbounded<CaptureWork>(
        new UnboundedChannelOptions { SingleReader = true });

    public ChannelReader<CaptureWork> Reader => _channel.Reader;

    public void Enqueue(CaptureRecord record) => _channel.Writer.TryWrite(new CapturedRequest(record));

    public void Enqueue(CaptureWork work) => _channel.Writer.TryWrite(work);

    /// <summary>Called on shutdown so the writer can drain what's left and stop.</summary>
    public void Complete() => _channel.Writer.TryComplete();
}
