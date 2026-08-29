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

    // R22/R11 — this single lock guards *all* of the hub's shared lifecycle state: the publish
    // id, the fan-out to subscriber channels, and the in-flight set.
    //
    // R22 (ordering): id allocation and the channel fan-out happen together under it, so every
    // subscriber observes ids strictly increasing. An atomic counter alone makes ids *unique*
    // but not *ordered* — two publishers could allocate N and N+1 and enqueue them reversed,
    // which the client reads as frame loss and answers with a needless reconciliation per
    // reversal (a storm during the exact burst reconciliation is meant to recover from).
    //
    // R11/H0b(2)/J0 (coherence): the in-flight set and the publish id are read *and* mutated
    // under this same lock, so `GetActiveRequests` returns one coherent snapshot — an active
    // set together with the log position it is true as of. That pairing is the whole recovery
    // contract (J0): every frame with `id <= LogPosition` is already reflected in the
    // returned descriptors, so a client can discard those and replay only what came after. Read
    // separately, a snapshot could pair a position with an active set from a different moment
    // — the review's 187/571 torn-snapshot probe — and reconciliation would then wrongly
    // expire a legitimate request. The lock is held for microseconds and never waits on a
    // subscriber (drop-oldest's TryWrite never blocks), so the non-blocking proxy contract is
    // unchanged.
    private readonly object _publishLock = new();
    private long _publishId;

    // I0b(1)/R11 — the request sequence counter lives here, not in CaptureContext, so that
    // allocating a seq and registering it in `_active` happen in one critical section. When
    // allocation sat in the CaptureContext constructor, a handler could be descheduled between
    // "seq 35 exists" and "seq 35 is registered", so a snapshot taken in that window reported
    // seq 35 neither active nor finished and the client expired a request that was about to
    // start running. With allocation inside `Register`, "a seq exists ⇒ it is registered" is
    // atomic and that interleaving is unrepresentable — which is what keeps J0's snapshot
    // honest: an unregistered seq cannot exist to be missing from one.
    private long _seqCounter;

    // R11/F2 — the server-authoritative in-flight set. An entry is added when its request
    // starts and removed when it completes (or is dropped), independent of whether anyone is
    // subscribed to the SSE feed. Reconciliation reads this to decide, authoritatively, which
    // client-side in-flight rows are genuinely still running versus finished-or-lost — the
    // client cannot infer that from paginated history alone (a completion off the loaded
    // pages, filtered out, or for a since-cleared row is simply invisible there). Guarded by
    // _publishLock (H0b(2)), never a concurrent collection, so it stays coherent with the
    // publish id read in the same critical section.
    //
    // K0b — the value is the request's own `started` payload, not a bare marker. The hub
    // already receives every field at registration, and a recovering client needs them to
    // *render* the request it is being told is running: knowing seq 2 is active is useless if
    // the `started` frame carrying its method, path and start time was the frame the bounded
    // queue dropped. Storing them costs one small immutable record per in-flight request, and
    // the terminal invariant (H0b(3)) already guarantees every entry is removed.
    private readonly Dictionary<long, ActiveDescriptor> _active = [];

    /// <summary>
    /// H0b(1) — an id unique to this server process (a fresh GUID per <see cref="CaptureEvents"/>
    /// construction, i.e. per Vessel run). Exposed on the SSE <c>hello</c> event and the
    /// <c>/active</c>/<c>/status</c> endpoints so a client can tell a restart from a mere
    /// reconnect: process-lifetime <c>seq</c>s <em>and</em> log positions both reset when the
    /// process does, so after a restart the old in-flight seqs and queued frame ids are
    /// meaningless and the client must discard them wholesale rather than compare them against
    /// the fresh process's numbering, where an old high value looks "just published".
    /// </summary>
    public string RunId { get; } = Guid.NewGuid().ToString("n");

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
    /// R11/F2/J0/K0b — lifecycle truth as of one stream position: the requests the server
    /// currently considers in-flight, each with the display metadata its <c>started</c> frame
    /// carried, together with the publish id that set is true as of. Recovery is wholesale
    /// replacement on the client: it rebuilds its in-flight rows from <c>Active</c> and
    /// discards every event it holds with <c>id &lt;= LogPosition</c>, because those are
    /// already reflected here; only later events replay on top. That replaces the retired
    /// boundary comparison (a completed-seq watermark), which could not order a client's
    /// *pending* work against the snapshot at all.
    /// </summary>
    public ActiveRequests GetActiveRequests()
    {
        lock (_publishLock)
        {
            // Both mutable fields read in one critical section — the whole point of H0b(2),
            // and what makes the position meaningful: nothing can be published between reading
            // the active set and reading the position it belongs to. RunId is immutable, but
            // travels with the snapshot so the client can reject one from a different process
            // lifetime. Ordered by seq, which is registration order, so a client rebuilding
            // its rows from this shows them in the same order the live feed would have.
            return new ActiveRequests([.. _active.Values.OrderBy(d => d.Seq)], _publishId, RunId);
        }
    }

    /// <summary>
    /// I0b(1)/D5 — allocates this request's <c>seq</c>, registers it as in-flight and emits
    /// <c>started</c>. Called at handler entry, once the backend/tags are resolved (request
    /// path); <paramref name="sessionId"/> is known here (D05) so the UI can scope in-flight
    /// rows to the viewed session instead of showing every session's live traffic.
    /// <para>
    /// Allocation happens <em>inside</em> this method, under the publish lock, so there is no
    /// window in which a seq exists without being registered (see <see cref="_seqCounter"/>).
    /// Registration is unconditional, regardless of subscribers: a client that receives the
    /// <c>started</c> frame must be able to trust the server had the seq in its active set at
    /// that moment, so the only reason a later reconciliation finds it absent is a genuine
    /// completion (R11/F2).
    /// </para>
    /// </summary>
    /// <returns>The newly allocated, already-registered request seq.</returns>
    public long Register(
        string startedAt, long sessionId, string method, string path, string backend, string[] tags, long? replayOf = null)
    {
        long seq;
        lock (_publishLock)
        {
            seq = ++_seqCounter;
            // K0b — registered *with* its display metadata, so the recovery snapshot can
            // describe this request even to a client that never received its `started` frame.
            // Model and TtftMs are both learned later (RequestReady, FirstToken respectively).
            _active.Add(seq, new ActiveDescriptor(seq, startedAt, sessionId, method, path, backend, tags, replayOf, null, null));
        }

        // Serialized and published outside the allocation section (JSON touches no shared
        // state) — and only if someone is watching, keeping the zero-subscriber hot path free
        // of JSON work. Splitting the two sections is safe in the one direction that matters:
        // a snapshot taken between them sees the seq active but no frame yet, never a frame
        // without the seq. That is exactly the direction J0's recovery needs: if this `started`
        // frame's id is at or below a snapshot's LogPosition, the seq was in that snapshot's
        // active set (or had already completed, whose frame is then also at or below it).
        // `completed` for this seq cannot overtake the frame either — it is
        // published by the writer only after this handler has enqueued the record. A
        // subscriber that connects inside this window simply misses one `started` frame, which
        // is exactly the drop/gap case its own reconciliation covers.
        if (!_subscribers.IsEmpty)
        {
            Publish("started", JsonSerializer.Serialize(
                new StartedEvent(seq, startedAt, sessionId, method, path, backend, tags, replayOf),
                EventsJsonContext.Default.StartedEvent));
        }

        return seq;
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
        // Serialize outside the lock (see Register for the rationale); the registry update
        // itself happens under it, in the same critical section as the frame's own id.
        string? json = _subscribers.IsEmpty
            ? null
            : JsonSerializer.Serialize(new RequestReadyEvent(seq, model), EventsJsonContext.Default.RequestReadyEvent);

        lock (_publishLock)
        {
            // K0b — the one field of the descriptor that is learned after registration. It is
            // recorded regardless of subscribers, for the same reason the registration is: a
            // client recovering after this position must see the model the frame carried, and
            // that frame may be exactly the one its bounded queue dropped.
            if (_active.TryGetValue(seq, out ActiveDescriptor? descriptor))
            {
                _active[seq] = descriptor with { Model = model };
            }

            if (json is not null)
            {
                PublishLocked("request_ready", json);
            }
        }
    }

    /// <summary>
    /// Emitted on the first-response-byte mark of streamed responses (request path).
    /// R27 — mirrors <see cref="RequestReady"/>: the locked active descriptor is updated
    /// regardless of subscribers, so a `first_token` frame a bounded subscriber queue drops
    /// is still recoverable from a later <see cref="GetActiveRequests"/> snapshot instead of
    /// being permanently lost to that client.
    /// </summary>
    public void FirstToken(long seq, double ttftMs)
    {
        // Serialize outside the lock (see Register for the rationale); the registry update
        // itself happens under it, in the same critical section as the frame's own id.
        string? json = _subscribers.IsEmpty
            ? null
            : JsonSerializer.Serialize(new FirstTokenEvent(seq, ttftMs), EventsJsonContext.Default.FirstTokenEvent);

        lock (_publishLock)
        {
            if (_active.TryGetValue(seq, out ActiveDescriptor? descriptor))
            {
                _active[seq] = descriptor with { TtftMs = ttftMs };
            }

            if (json is not null)
            {
                PublishLocked("first_token", json);
            }
        }
    }

    /// <summary>
    /// Emitted by the writer after the row is inserted (background). <paramref name="row"/>
    /// is null when the writer dropped the batch (resilience path) — the UI clears the
    /// in-flight entry rather than showing it as complete.
    /// </summary>
    public void Completed(long seq, Summary? row)
    {
        // Serialize outside the lock (see Started for the subscriber-race rationale).
        string? json = _subscribers.IsEmpty
            ? null
            : JsonSerializer.Serialize(new CompletedEvent(seq, row), EventsJsonContext.Default.CompletedEvent);

        lock (_publishLock)
        {
            // Leave the active registry unconditionally (a drop still calls this with row == null),
            // so reconciliation sees the request as finished even for a client that was never
            // subscribed when it ran (R11/F2). The removal and the fan-out are in the one
            // critical section GetActiveRequests reads under, so a snapshot never pairs a
            // position at or past this frame with an active set that still contains the seq.
            _active.Remove(seq);

            if (json is not null)
            {
                PublishLocked("completed", json);
            }
        }
    }

    /// <summary>
    /// H0a/R23/J0 — the in-band clear notification: history was deleted at this position in the
    /// log. Emitted by the writer at clear-commit time, under the same publish lock as
    /// <c>completed</c>, so its id orders correctly against every completion.
    /// <para>
    /// J0 — the frame is the fast path and carries <em>no recovery burden</em>: its payload is
    /// empty, and the server retains no clear predicate, version or history at all. A client
    /// that receives it drops what it holds at that position and refetches; a client that
    /// misses it recovers by snapshot instead, and the refetch that recovery schedules reads a
    /// database which already reflects every clear that ever ran. The retired I0a model (a
    /// versioned <c>{scope, beforeTs, boundaryId}</c> predicate the client re-applied) could
    /// not describe *several* missed clears, and its id prefix purged legitimate rows once
    /// SQLite reused ids after a clear-all — the round-five review's §2.2 B and C.
    /// </para>
    /// </summary>
    public void Cleared()
    {
        if (_subscribers.IsEmpty)
        {
            return;
        }

        // Nothing to serialize: the frame's position in the log is its entire content.
        Publish("cleared", "{}");
    }

    private void Publish(string name, string json)
    {
        lock (_publishLock)
        {
            PublishLocked(name, json);
        }
    }

    /// <summary>
    /// R22 — allocate the id and fan out in one critical section (the caller already holds
    /// <see cref="_publishLock"/>), so every subscriber's queue receives frames in id order.
    /// One id per published frame, shared by every subscriber: all subscribers receive the same
    /// fan-out, so a per-subscriber gap in this sequence means that subscriber's queue dropped
    /// frames (R11).
    /// </summary>
    private void PublishLocked(string name, string json)
    {
        var evt = new SseEvent(++_publishId, name, json);
        foreach (Channel<SseEvent> channel in _subscribers.Values)
        {
            channel.Writer.TryWrite(evt); // drop-oldest mode never blocks
        }
    }
}

/// <summary>
/// R11/F2/J0/K0b — lifecycle truth as of one position in the event log.
/// <paramref name="Active"/> is the requests running at that position, in seq order, each
/// carrying the metadata its <c>started</c> frame carried;
/// <paramref name="LogPosition"/> is the newest SSE publish id allocated when the snapshot was
/// taken, so every frame at or below it is already reflected in <paramref name="Active"/>
/// and a recovering client replays only what came after it;
/// <paramref name="ServerRunId"/> (H0b(1)) identifies the process lifetime the seqs and
/// positions belong to, so a client can discard a snapshot from a different Vessel run outright.
/// </summary>
public sealed record ActiveRequests(ActiveDescriptor[] Active, long LogPosition, string ServerRunId);

/// <summary>
/// K0b/R11 — one in-flight request as the recovery snapshot describes it: its <c>seq</c> plus
/// the immutable payload of its <c>started</c> frame, <paramref name="Model"/> once
/// <c>request_ready</c> has parsed one (null until then, and for requests whose body carries
/// no parseable model), and <paramref name="TtftMs"/> once <c>first_token</c> has fired (null
/// until then, and for a request still waiting on its first byte).
/// <para>
/// The snapshot carries these because a bare seq is not enough to *show* the request. The SSE
/// feed is deliberately lossy, so the frame that would have supplied the method, path, start
/// time, session, tags, model or live TTFT is exactly the frame a recovering client may have
/// missed — and a monitor that knows a request is running but cannot display its measured
/// progress is not monitoring it (R27).
/// </para>
/// </summary>
public sealed record ActiveDescriptor(
    long Seq,
    string StartedAt,
    long SessionId,
    string Method,
    string Path,
    string Backend,
    string[] Tags,
    long? ReplayOf,
    string? Model,
    double? TtftMs);

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
    long Seq, string StartedAt, long SessionId, string Method, string Path, string Backend, string[] Tags, long? ReplayOf);

internal sealed record RequestReadyEvent(long Seq, string Model);

internal sealed record FirstTokenEvent(long Seq, double TtftMs);

internal sealed record CompletedEvent(long Seq, Summary? Row);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(StartedEvent))]
[JsonSerializable(typeof(RequestReadyEvent))]
[JsonSerializable(typeof(FirstTokenEvent))]
[JsonSerializable(typeof(CompletedEvent))]
internal sealed partial class EventsJsonContext : JsonSerializerContext;
