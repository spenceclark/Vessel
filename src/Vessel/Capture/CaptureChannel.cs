using System.Threading.Channels;

namespace Vessel.Capture;

/// <summary>
/// Thrown into a control command's completion when the writer has given up. Endpoints
/// surface it rather than awaiting a completion nobody will ever resolve.
/// </summary>
public sealed class CaptureStoppedException(string message) : Exception(message);

/// <summary>
/// The unbounded queue between the request path and the background writer. Enqueue is
/// fire-and-forget from the request's point of view; the writer is the single reader.
/// Carries the <see cref="CaptureWork"/> union — captured requests and, off the request
/// path, writer-thread control commands (D4).
/// <para>
/// R06: the writer gives up after <c>MaxConsecutiveFailures</c>, and previously just
/// returned — leaving this channel open and unread. Captures kept accumulating (retained
/// bodies, unbounded), and clear/session commands awaited completions nobody would ever
/// resolve, with HTTP cancellation not bounding the wait. <see cref="Stop"/> makes that
/// terminal state explicit: captures are dropped at admission instead of queued, and every
/// command fails fast with <see cref="CaptureStoppedException"/>. Forwarding is never
/// blocked either way — dropping capture is the agreed failure policy.
/// </para>
/// </summary>
public sealed class CaptureChannel
{
    private readonly Channel<CaptureWork> _channel = Channel.CreateUnbounded<CaptureWork>(
        new UnboundedChannelOptions { SingleReader = true });

    private volatile string? _stoppedReason;

    public ChannelReader<CaptureWork> Reader => _channel.Reader;

    /// <summary>R06 — null while recording; the operator-facing reason once the writer has given up.</summary>
    public string? StoppedReason => _stoppedReason;

    public bool IsStopped => _stoppedReason is not null;

    /// <summary>
    /// Admits a captured request. Returns false when admission is closed (the writer gave up)
    /// so the caller can drive the capture's lifecycle to a terminal state itself — the writer
    /// will never emit <c>completed</c> for a capture it never received, and a registered-but-
    /// never-completed seq leaks in the active set forever (R25).
    /// </summary>
    public bool Enqueue(CaptureRecord record) => Enqueue(new CapturedRequest(record));

    /// <summary>
    /// Admits one unit of work. Returns true when it was queued for the writer, false when
    /// admission is closed (a command is failed fast in that case; a capture is simply dropped,
    /// which is what "capture stopped" means — the caller owns its terminal transition, R25).
    /// </summary>
    public bool Enqueue(CaptureWork work)
    {
        if (_stoppedReason is string reason)
        {
            // Never queue behind a consumer that is gone. A command still gets a definite
            // answer; a capture is simply dropped.
            FailIfCommand(work, reason);
            return false;
        }

        if (!_channel.Writer.TryWrite(work))
        {
            FailIfCommand(work, "capture queue is closed");
            return false;
        }

        return true;
    }

    /// <summary>
    /// R06 — enter the terminal state: stop admitting work and record why. The writer then
    /// drains whatever raced in and fails those commands too (see
    /// <c>CaptureWriterService.DrainAfterStop</c>), so nothing is left awaiting forever.
    /// </summary>
    public void Stop(string reason)
    {
        _stoppedReason = reason;
        _channel.Writer.TryComplete();
    }

    /// <summary>Called on shutdown so the writer can drain what's left and stop.</summary>
    public void Complete() => _channel.Writer.TryComplete();

    /// <summary>Commands carry a completion an HTTP handler is awaiting; captures carry nothing to answer.</summary>
    internal static void FailIfCommand(CaptureWork work, string reason)
    {
        switch (work)
        {
            case CreateSessionCommand command:
                command.Completion.TrySetException(new CaptureStoppedException(reason));
                break;
            case ClearCommand command:
                command.Completion.TrySetException(new CaptureStoppedException(reason));
                break;
            case DeleteSessionCommand command:
                command.Completion.TrySetException(new CaptureStoppedException(reason));
                break;
        }
    }
}
