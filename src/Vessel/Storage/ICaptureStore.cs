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
    /// <paramref name="beforeIso"/>, or every row when null. Returns the number of
    /// <c>requests</c> rows deleted and the highest id among them (R23: the client uses that
    /// boundary to discard buffered completions the clear invalidated while keeping ones that
    /// finished above it).
    /// </summary>
    ClearOutcome Clear(string? beforeIso);
}

/// <summary>
/// R23 — the result of a clear. <paramref name="MaxDeletedId"/> is the highest <c>requests</c>
/// id that was deleted (null when nothing matched), the deletion boundary a clear-before
/// hands the client so a completion buffered for a row above it survives.
/// </summary>
public readonly record struct ClearOutcome(int Deleted, long? MaxDeletedId);
