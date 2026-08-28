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
    /// <paramref name="beforeIso"/>, or every row when null, and returns the count deleted.
    /// <para>
    /// R23/H0a: the client no longer infers a deletion boundary from a max-id here — that
    /// approach was wrong (ids follow persistence order, not start time, so a clear-before
    /// couldn't be described by an id boundary). The client instead purges on the in-band
    /// <c>cleared</c> SSE event, using the server's own <c>started_at &lt; beforeIso</c>
    /// predicate; the returned count is UX only (the "Deleted N" toast).
    /// </para>
    /// </summary>
    int Clear(string? beforeIso);
}
