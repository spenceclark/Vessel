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
    // id, the fan-out to subscriber channels, the in-flight set, and the completed watermark.
    //
    // R22 (ordering): id allocation and the channel fan-out happen together under it, so every
    // subscriber observes ids strictly increasing. An atomic counter alone makes ids *unique*
    // but not *ordered* — two publishers could allocate N and N+1 and enqueue them reversed,
    // which the client reads as frame loss and answers with a needless reconciliation per
    // reversal (a storm during the exact burst reconciliation is meant to recover from).
    //
    // R11/H0b(2) (coherence): the in-flight set and the completed watermark are read *and*
    // mutated under this same lock, so `GetActiveRequests` returns one coherent snapshot. Read
    // separately (a concurrent dictionary + an interlocked long), a snapshot could report a
    // watermark that already covers a still-running seq missing from the returned key array —
    // the review's 187/571 torn-snapshot probe — and reconciliation would then wrongly expire
    // a legitimate request. The lock is held for microseconds and never waits on a subscriber
    // (drop-oldest's TryWrite never blocks), so the non-blocking proxy contract is unchanged.
    private readonly object _publishLock = new();
    private long _publishId;

    // R11/F2 — the server-authoritative in-flight set. A seq is added when its request starts
    // and removed when it completes (or is dropped), independent of whether anyone is
    // subscribed to the SSE feed. Reconciliation reads this to decide, authoritatively, which
    // client-side in-flight rows are genuinely still running versus finished-or-lost — the
    // client cannot infer that from paginated history alone (a completion off the loaded
    // pages, filtered out, or for a since-cleared row is simply invisible there). Guarded by
    // _publishLock (H0b(2)), never a concurrent collection, so it stays coherent with the
    // watermark read in the same critical section.
    private readonly HashSet<long> _active = [];
    private long _newestCompletedSeq;

    /// <summary>
    /// H0b(1) — an id unique to this server process (a fresh GUID per <see cref="CaptureEvents"/>
    /// construction, i.e. per Vessel run). Exposed on the SSE <c>hello</c> event and the
    /// <c>/active</c>/<c>/status</c> endpoints so a client can tell a restart from a mere
    /// reconnect: process-lifetime <c>seq</c>s reset when the process does, so after a restart
    /// the old in-flight seqs are meaningless and the client must discard them wholesale — a
    /// boundary comparison against the fresh process's watermark can't, since an old high seq
    /// sits above the new low watermark and looks "just started".
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
    /// R11/F2 — a snapshot of the requests the server currently considers in-flight, plus the
    /// newest seq that has completed. Reconciliation removes a client-side in-flight row only
    /// when its seq is absent from <see cref="ActiveRequests.ActiveSeqs"/> <em>and</em> at or
    /// below <see cref="ActiveRequests.NewestCompletedSeq"/> — the boundary guards a request
    /// that started after this snapshot was taken (its <c>started</c> frame can reach the
    /// client before the next reconciliation observes it as active) from being expired as if
    /// it had finished.
    /// </summary>
    public ActiveRequests GetActiveRequests()
    {
        lock (_publishLock)
        {
            // Both fields read in one critical section — the whole point of H0b(2). RunId is
            // immutable, but travels with the snapshot so the client can reject a snapshot from
            // a different process lifetime.
            return new ActiveRequests([.. _active], _newestCompletedSeq, RunId);
        }
    }

    /// <summary>
    /// Emitted at handler entry, once the backend/tags are resolved (request path).
    /// <paramref name="sessionId"/> is known here (D05) so the UI can scope in-flight rows
    /// to the viewed session instead of showing every session's live traffic.
    /// </summary>
    public void Started(
        long seq, string startedAt, long sessionId, string method, string path, string backend, string[] tags)
    {
        // Serialize outside the lock (touches no shared state) — but only if someone is
        // watching, keeping the zero-subscriber hot path free of JSON work. A subscriber that
        // connects in the tiny window between this check and the lock simply misses this one
        // `started` frame, which is exactly the drop/gap case its own reconciliation covers.
        string? json = _subscribers.IsEmpty
            ? null
            : JsonSerializer.Serialize(
                new StartedEvent(seq, startedAt, sessionId, method, path, backend, tags),
                EventsJsonContext.Default.StartedEvent);

        lock (_publishLock)
        {
            // Register unconditionally (regardless of subscribers): a client that receives this
            // `started` frame must be able to trust the server had the seq in its active set at
            // that moment, so the only reason a later reconciliation finds it absent is a
            // genuine completion (R11/F2). The registration and the fan-out share the lock, so
            // a concurrent GetActiveRequests can never observe the frame without the seq.
            _active.Add(seq);
            if (json is not null)
            {
                PublishLocked("started", json);
            }
        }
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
        // Serialize outside the lock (see Started for the subscriber-race rationale).
        string? json = _subscribers.IsEmpty
            ? null
            : JsonSerializer.Serialize(new CompletedEvent(seq, row), EventsJsonContext.Default.CompletedEvent);

        lock (_publishLock)
        {
            // Leave the active set and advance the watermark unconditionally (a drop still calls
            // this with row == null), so reconciliation sees the request as finished even for a
            // client that was never subscribed when it ran (R11/F2). Both mutations, and the
            // fan-out, are in the one critical section GetActiveRequests reads under.
            _active.Remove(seq);
            if (seq > _newestCompletedSeq)
            {
                _newestCompletedSeq = seq;
            }

            if (json is not null)
            {
                PublishLocked("completed", json);
            }
        }
    }

    /// <summary>
    /// H0a/R23 — the in-band clear notification. Emitted by the writer at clear-commit time,
    /// under the same publish lock as <c>completed</c>, so its id orders correctly against
    /// every completion. Because a row can only be *deleted* by a clear if it was inserted
    /// (hence its <c>completed</c> published) before the clear ran, the client is guaranteed to
    /// see <c>completed</c> for a doomed row before this <c>cleared</c> frame — the ordering is
    /// what lets it purge exactly the cleared rows and treat everything after as post-clear by
    /// construction (covering SQLite id reuse). Replaces the retired boundary/generation model.
    /// </summary>
    public void Cleared(string? beforeIso)
    {
        if (_subscribers.IsEmpty)
        {
            return;
        }

        Publish("cleared", JsonSerializer.Serialize(
            new ClearedEvent(beforeIso is null ? "all" : "before", beforeIso),
            EventsJsonContext.Default.ClearedEvent));
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
/// R11/F2 — a point-in-time view of the server's in-flight requests for reconciliation.
/// <paramref name="ActiveSeqs"/> is the set of request seqs currently running;
/// <paramref name="NewestCompletedSeq"/> is the highest seq that has finished, used as the
/// boundary below which an absent seq is definitely finished rather than just newly started;
/// <paramref name="ServerRunId"/> (H0b(1)) identifies the process lifetime the seqs belong to,
/// so a client can discard the whole set when it came from a different Vessel run.
/// </summary>
public sealed record ActiveRequests(long[] ActiveSeqs, long NewestCompletedSeq, string ServerRunId);

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

/// <summary>
/// H0a/R23 — the <c>cleared</c> event payload. <paramref name="Scope"/> is <c>"all"</c> or
/// <c>"before"</c>; <paramref name="BeforeTs"/> is the ISO-8601 cutoff for a clear-before
/// (null for clear-all), the same predicate the server deleted by, so the client purges
/// exactly the rows the server removed.
/// </summary>
internal sealed record ClearedEvent(
    string Scope,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? BeforeTs);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(StartedEvent))]
[JsonSerializable(typeof(RequestReadyEvent))]
[JsonSerializable(typeof(FirstTokenEvent))]
[JsonSerializable(typeof(CompletedEvent))]
[JsonSerializable(typeof(ClearedEvent))]
internal sealed partial class EventsJsonContext : JsonSerializerContext;
