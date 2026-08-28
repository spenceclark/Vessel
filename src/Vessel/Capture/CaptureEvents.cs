using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Vessel.Storage;

namespace Vessel.Capture;

/// <summary>
/// One pre-serialized SSE frame: a monotonic publish id, a named event, and its
/// single-line JSON payload.
/// <para>
/// R11 — <see cref="Id"/> is what makes event loss *detectable*. Subscriber queues drop
/// oldest when full (deliberately, so a stalled browser can't back-pressure the request
/// path), and a client that only removes an in-flight row on <c>completed</c> had no way
/// to know a completion had been dropped: the row ran forever. The id is emitted as the
/// SSE <c>id:</c> field, so a client seeing a jump knows exactly that it missed frames and
/// can reconcile authoritatively. It is deliberately *not* the request <c>seq</c>: seq is
/// assigned at request start, so a long-running request legitimately trails far behind the
/// newest seq, and any distance heuristic on it would expire real in-flight requests.
/// </para>
/// </summary>
public sealed record SseEvent(long Id, string Name, string Json);

/// <summary>
/// D5 — the SSE broadcast hub. Each subscriber (one per open <c>/vessel/api/events</c>
/// connection) gets its own bounded, drop-oldest channel so a stalled browser can never
/// back-pressure the request path or the writer. With zero subscribers, every publish
/// method is a single dictionary emptiness check — near-zero cost on the hot path.
/// </summary>
public sealed class CaptureEvents
{
    private const int SubscriberCapacity = 256;

    private readonly ConcurrentDictionary<long, Channel<SseEvent>> _subscribers = new();
    private long _nextSubscriberId;

    // R22 — id allocation and the channel fan-out happen together under this lock, so every
    // subscriber observes ids in strictly increasing order. An atomic counter alone makes ids
    // *unique* but not *ordered*: two publishers could allocate N and N+1 and enqueue them
    // reversed, which the client reads as frame loss and answers with a needless
    // reconciliation for every reversal — a storm during the exact burst reconciliation is
    // meant to recover from. The lock is held for microseconds and never waits on a
    // subscriber (drop-oldest's TryWrite never blocks), so the non-blocking proxy contract is
    // unchanged.
    private readonly object _publishLock = new();
    private long _publishId;

    // R11/F2 — the server-authoritative in-flight set. A seq is added when its request starts
    // and removed when it completes (or is dropped), independent of whether anyone is
    // subscribed to the SSE feed. Reconciliation reads this to decide, authoritatively, which
    // client-side in-flight rows are genuinely still running versus finished-or-lost — the
    // client cannot infer that from paginated history alone (a completion off the loaded
    // pages, filtered out, or for a since-cleared row is simply invisible there).
    private readonly ConcurrentDictionary<long, byte> _active = new();
    private long _newestCompletedSeq;

    public CaptureSubscription Subscribe()
    {
        long id = Interlocked.Increment(ref _nextSubscriberId);
        var channel = Channel.CreateBounded<SseEvent>(new BoundedChannelOptions(SubscriberCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
        });
        _subscribers[id] = channel;
        return new CaptureSubscription(this, id, channel.Reader);
    }

    internal void Unsubscribe(long id) => _subscribers.TryRemove(id, out _);

    /// <summary>
    /// R11/F2 — a snapshot of the requests the server currently considers in-flight, plus the
    /// newest seq that has completed. Reconciliation removes a client-side in-flight row only
    /// when its seq is absent from <see cref="ActiveRequests.ActiveSeqs"/> <em>and</em> at or
    /// below <see cref="ActiveRequests.NewestCompletedSeq"/> — the boundary guards a request
    /// that started after this snapshot was taken (its <c>started</c> frame can reach the
    /// client before the next reconciliation observes it as active) from being expired as if
    /// it had finished.
    /// </summary>
    public ActiveRequests GetActiveRequests() =>
        new(_active.Keys.ToArray(), Interlocked.Read(ref _newestCompletedSeq));

    /// <summary>
    /// Emitted at handler entry, once the backend/tags are resolved (request path).
    /// <paramref name="sessionId"/> is known here (D05) so the UI can scope in-flight rows
    /// to the viewed session instead of showing every session's live traffic.
    /// </summary>
    public void Started(
        long seq, string startedAt, long sessionId, string method, string path, string backend, string[] tags)
    {
        // Register before publishing (and unconditionally, regardless of subscribers): a
        // client that receives this `started` frame must be able to trust that the server had
        // the seq in its active set at that moment, so the only reason a later reconciliation
        // finds it absent is a genuine completion (R11/F2).
        _active[seq] = 0;

        if (_subscribers.IsEmpty)
        {
            return;
        }

        Publish("started", JsonSerializer.Serialize(
            new StartedEvent(seq, startedAt, sessionId, method, path, backend, tags),
            EventsJsonContext.Default.StartedEvent));
    }

    /// <summary>
    /// Post-Phase-4 addition (ui-spec.md §9.1, phase-3.md D5) — emitted once the request
    /// body is fully read and its <c>model</c> field parsed off the request path (a
    /// background task; see <see cref="CaptureContext.EmitRequestReadyIfParseable"/>).
    /// Exists so an in-flight row can show the real model within milliseconds of
    /// dispatch instead of only after completion.
    /// </summary>
    public void RequestReady(long seq, string model)
    {
        if (_subscribers.IsEmpty)
        {
            return;
        }

        Publish("request_ready", JsonSerializer.Serialize(
            new RequestReadyEvent(seq, model), EventsJsonContext.Default.RequestReadyEvent));
    }

    /// <summary>Emitted on the first-response-byte mark of streamed responses (request path).</summary>
    public void FirstToken(long seq, double ttftMs)
    {
        if (_subscribers.IsEmpty)
        {
            return;
        }

        Publish("first_token", JsonSerializer.Serialize(
            new FirstTokenEvent(seq, ttftMs), EventsJsonContext.Default.FirstTokenEvent));
    }

    /// <summary>
    /// Emitted by the writer after the row is inserted (background). <paramref name="row"/>
    /// is null when the writer dropped the batch (resilience path) — the UI clears the
    /// in-flight entry rather than showing it as complete.
    /// </summary>
    public void Completed(long seq, Summary? row)
    {
        // Leave the active set and advance the completed watermark unconditionally (a drop
        // still calls this with row == null), so reconciliation sees the request as finished
        // even for a client that was never subscribed when it ran (R11/F2).
        _active.TryRemove(seq, out _);
        AdvanceNewestCompleted(seq);

        if (_subscribers.IsEmpty)
        {
            return;
        }

        Publish("completed", JsonSerializer.Serialize(
            new CompletedEvent(seq, row), EventsJsonContext.Default.CompletedEvent));
    }

    private void AdvanceNewestCompleted(long seq)
    {
        long current = Interlocked.Read(ref _newestCompletedSeq);
        while (seq > current)
        {
            long observed = Interlocked.CompareExchange(ref _newestCompletedSeq, seq, current);
            if (observed == current)
            {
                break;
            }

            current = observed;
        }
    }

    private void Publish(string name, string json)
    {
        // R22 — allocate the id and fan out in one critical section, so every subscriber's
        // queue receives frames in id order. One id per published frame, shared by every
        // subscriber: all subscribers receive the same fan-out, so a per-subscriber gap in
        // this sequence means that subscriber's queue dropped frames (R11).
        lock (_publishLock)
        {
            var evt = new SseEvent(Interlocked.Increment(ref _publishId), name, json);
            foreach (Channel<SseEvent> channel in _subscribers.Values)
            {
                channel.Writer.TryWrite(evt); // drop-oldest mode never blocks
            }
        }
    }
}

/// <summary>
/// R11/F2 — a point-in-time view of the server's in-flight requests for reconciliation.
/// <paramref name="ActiveSeqs"/> is the set of request seqs currently running;
/// <paramref name="NewestCompletedSeq"/> is the highest seq that has finished, used as the
/// boundary below which an absent seq is definitely finished rather than just newly started.
/// </summary>
public sealed record ActiveRequests(long[] ActiveSeqs, long NewestCompletedSeq);

/// <summary>One SSE connection's subscription; disposing unregisters it from the hub.</summary>
public sealed class CaptureSubscription : IDisposable
{
    private readonly CaptureEvents _hub;
    private readonly long _id;

    internal CaptureSubscription(CaptureEvents hub, long id, ChannelReader<SseEvent> reader)
    {
        _hub = hub;
        _id = id;
        Reader = reader;
    }

    public ChannelReader<SseEvent> Reader { get; }

    public void Dispose() => _hub.Unsubscribe(_id);
}

internal sealed record StartedEvent(
    long Seq, string StartedAt, long SessionId, string Method, string Path, string Backend, string[] Tags);

internal sealed record RequestReadyEvent(long Seq, string Model);

internal sealed record FirstTokenEvent(long Seq, double TtftMs);

internal sealed record CompletedEvent(long Seq, Summary? Row);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(StartedEvent))]
[JsonSerializable(typeof(RequestReadyEvent))]
[JsonSerializable(typeof(FirstTokenEvent))]
[JsonSerializable(typeof(CompletedEvent))]
internal sealed partial class EventsJsonContext : JsonSerializerContext;
