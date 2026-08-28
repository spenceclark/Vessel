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

    // I0b(1)/R11 — the request sequence counter lives here, not in CaptureContext, so that
    // allocating a seq and registering it in `_active` happen in one critical section. When
    // allocation sat in the CaptureContext constructor, a handler could be descheduled between
    // "seq 35 exists" and "seq 35 is registered"; a request that started later (seq 36) could
    // complete in that window and advance the watermark past 35, and a snapshot taken there
    // reported neither 35 active nor 35 unfinished — the client's boundary rule then expired a
    // request that was about to start running. With allocation inside `Register`, "a seq exists
    // ⇒ it is registered" is atomic and that interleaving is unrepresentable.
    private long _seqCounter;

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

    // I0a/R23 — the latest clear, as a version plus the predicate the server actually deleted
    // by. The `cleared` frame is the fast path, but it rides a deliberately lossy feed: a
    // client whose queue dropped it (or that was mid-recovery) learns the same clear from
    // `GET /active`, compares versions, and re-applies the predicate. Correctness therefore
    // never depends on that frame surviving. Guarded by _publishLock like every other piece of
    // shared lifecycle state, so an /active snapshot's clear state is coherent with the frames
    // that subscriber has already been sent.
    private ClearState? _clear;
    private long _clearVersion;

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
            // Every field read in one critical section — the whole point of H0b(2). RunId is
            // immutable, but travels with the snapshot so the client can reject a snapshot from
            // a different process lifetime; the clear state (I0a) rides along so recovery can
            // re-apply a clear whose in-band frame was dropped.
            return new ActiveRequests([.. _active], _newestCompletedSeq, RunId, _clear);
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
        string startedAt, long sessionId, string method, string path, string backend, string[] tags)
    {
        long seq;
        lock (_publishLock)
        {
            seq = ++_seqCounter;
            _active.Add(seq);
        }

        // Serialized and published outside the allocation section (JSON touches no shared
        // state) — and only if someone is watching, keeping the zero-subscriber hot path free
        // of JSON work. Splitting the two sections is safe in the one direction that matters:
        // a snapshot taken between them sees the seq active but no frame yet, never a frame
        // without the seq. `completed` for this seq cannot overtake the frame either — it is
        // published by the writer only after this handler has enqueued the record. A
        // subscriber that connects inside this window simply misses one `started` frame, which
        // is exactly the drop/gap case its own reconciliation covers.
        if (!_subscribers.IsEmpty)
        {
            Publish("started", JsonSerializer.Serialize(
                new StartedEvent(seq, startedAt, sessionId, method, path, backend, tags),
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
    /// <param name="beforeIso">The clear-before cutoff, or null for a clear-all.</param>
    /// <param name="boundaryId">
    /// I0a — for a clear-all, the largest row id that existed when the DELETE ran: every
    /// deleted row has <c>id ≤ boundaryId</c>, which makes an id prefix a valid (necessary)
    /// condition there, and bounds what a re-applied predicate can touch. Unused for a
    /// clear-before, whose predicate is the <c>started_at</c> cutoff the server deleted by.
    /// </param>
    public void Cleared(string? beforeIso, long boundaryId)
    {
        // Unlike the other publishers this serializes *inside* the lock, deliberately: the
        // version is allocated there and the frame carries it, and the strict "every deleted
        // row's `completed` precedes this frame" ordering is the whole point. Clears are a
        // rare user action, so the JSON work under the lock costs nothing that matters.
        lock (_publishLock)
        {
            var state = new ClearState(
                ++_clearVersion, beforeIso is null ? "all" : "before", beforeIso, boundaryId);
            _clear = state;

            if (!_subscribers.IsEmpty)
            {
                PublishLocked("cleared", JsonSerializer.Serialize(
                    new ClearedEvent(state.Version, state.Scope, state.BeforeTs, state.BoundaryId),
                    EventsJsonContext.Default.ClearedEvent));
            }
        }
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
/// so a client can discard the whole set when it came from a different Vessel run;
/// <paramref name="Clear"/> (I0a) is the latest clear this run performed, or null if none, so
/// a client that missed the in-band <c>cleared</c> frame can still apply it.
/// </summary>
public sealed record ActiveRequests(
    long[] ActiveSeqs, long NewestCompletedSeq, string ServerRunId, ClearState? Clear);

/// <summary>
/// I0a/R23 — one clear, as a monotonic version plus the predicate the server deleted by.
/// <paramref name="Version"/> increases by one per clear within a run (paired with the run id,
/// since it resets when the process does); <paramref name="Scope"/> is <c>"all"</c> or
/// <c>"before"</c>; <paramref name="BeforeTs"/> is the clear-before cutoff (null for
/// clear-all); <paramref name="BoundaryId"/> is the largest row id that existed at clear-all
/// time (0 for a clear-before).
/// </summary>
public sealed record ClearState(long Version, string Scope, string? BeforeTs, long BoundaryId);

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
/// H0a/R23/I0a — the <c>cleared</c> event payload: the same versioned predicate
/// <c>GET /active</c> reports, so the in-band frame and recovery describe one clear
/// identically. <paramref name="Version"/> lets a client tell "already applied" from
/// "missed"; <paramref name="Scope"/> is <c>"all"</c> or <c>"before"</c>;
/// <paramref name="BeforeTs"/> is the ISO-8601 cutoff for a clear-before (null for
/// clear-all); <paramref name="BoundaryId"/> is the clear-all id boundary.
/// </summary>
internal sealed record ClearedEvent(long Version, string Scope, string? BeforeTs, long BoundaryId);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(StartedEvent))]
[JsonSerializable(typeof(RequestReadyEvent))]
[JsonSerializable(typeof(FirstTokenEvent))]
[JsonSerializable(typeof(CompletedEvent))]
[JsonSerializable(typeof(ClearedEvent))]
internal sealed partial class EventsJsonContext : JsonSerializerContext;
