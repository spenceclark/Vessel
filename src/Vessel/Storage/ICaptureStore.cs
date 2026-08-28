using Vessel.Formats;

namespace Vessel.Storage;

/// <summary>
/// The writer's view of the capture store. A seam over <see cref="SqliteCaptureStore"/> so
/// the writer's resilience (drop a failing batch, give up after N in a row) can be tested
/// against a store that throws on demand.
/// </summary>
public interface ICaptureStore
{
    void Initialize();

    /// <summary>Inserts the batch and returns each row's new id, in the same order as <paramref name="batch"/>.</summary>
    IReadOnlyList<long> InsertBatch(IReadOnlyList<EnrichedRecord> batch);

    void EnforceRetention();

    /// <summary>D4 — the newest <c>sessions</c> row, or a freshly created "session 1" on an empty database.</summary>
    SessionInfo EnsureInitialSession();

    /// <summary>D4 — inserts a new session marker row (the writer-thread-safe half of <c>POST /sessions</c>).</summary>
    SessionInfo CreateSession(string? name);

    /// <summary>
    /// D6 — deletes <c>requests</c> rows (and their FTS rows) matching
    /// <paramref name="beforeIso"/>, or every row when null.
    /// <para>
    /// R23/H0a: the client is never handed a deletion boundary to infer scope from — that
    /// approach was wrong (ids follow persistence order, not start time, so a clear-before
    /// cannot be described by an id boundary). It purges by the server's own predicate,
    /// delivered on the in-band <c>cleared</c> SSE event and repeated by <c>GET /active</c>
    /// (I0a); <see cref="ClearResult.Deleted"/> is UX only (the "Deleted N" toast).
    /// </para>
    /// </summary>
    ClearResult Clear(string? beforeIso);
}

/// <summary>
/// I0a/R23 — the outcome of one clear. <paramref name="Deleted"/> is the row count, for the
/// ack's toast. <paramref name="BoundaryId"/> is the largest <c>requests.id</c> that existed
/// immediately before a <em>clear-all</em> ran, so every deleted row satisfies
/// <c>id ≤ BoundaryId</c> — a valid necessary condition that bounds what the client's
/// re-applied purge can touch. It is 0 for a clear-before, whose predicate is the
/// <c>started_at</c> cutoff itself, and it is never a *sufficient* condition on its own:
/// SQLite reuses row ids after a clear-all empties the table, so a fresh row can sit below the
/// boundary (see <c>useLiveHistory</c>, which pairs it with post-clear provenance).
/// </summary>
public readonly record struct ClearResult(int Deleted, long BoundaryId);
