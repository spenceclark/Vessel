using System.Threading.Channels;

namespace Vessel.Capture;

/// <summary>
/// The unbounded queue between the request path and the background writer. Enqueue is
/// fire-and-forget from the request's point of view; the writer is the single reader.
/// </summary>
public sealed class CaptureChannel
{
    private readonly Channel<CaptureRecord> _channel = Channel.CreateUnbounded<CaptureRecord>(
        new UnboundedChannelOptions { SingleReader = true });

    public ChannelReader<CaptureRecord> Reader => _channel.Reader;

    public void Enqueue(CaptureRecord record) => _channel.Writer.TryWrite(record);

    /// <summary>Called on shutdown so the writer can drain what's left and stop.</summary>
    public void Complete() => _channel.Writer.TryComplete();
}
