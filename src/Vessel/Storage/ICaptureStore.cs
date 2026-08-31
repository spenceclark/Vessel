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

    void EnforceRetention(IReadOnlySet<long>? protectedSessionIds = null);

    /// <summary>D4 — the newest <c>sessions</c> row, or a freshly created "session 1" on an empty database.</summary>
    SessionInfo EnsureInitialSession();

    /// <summary>D4 — inserts a new session marker row (the writer-thread-safe half of <c>POST /sessions</c>).</summary>
    SessionInfo CreateSession(string? name);

    /// <summary>
    /// Resolves a per-request session name to its existing marker, or creates it on first
    /// sight. Called only by the capture writer, so lookup-or-create needs no second lock.
    /// It does not change the Reset-driven current session.
    /// </summary>
    NamedSessionResolution ResolveNamedSession(string name);

    /// <summary>
    /// D6 — deletes <c>requests</c> rows (and their FTS rows) matching
    /// <paramref name="beforeIso"/>, or every row when null, and returns the row count.
    /// <para>
    /// R23/H0a/J0: the client is never handed a deletion predicate to re-apply — every such
    /// model was wrong in some ordering (an id boundary cannot describe a clear-before; a
    /// latest-predicate cannot describe two missed clears; an id prefix purges live rows once
    /// SQLite reuses ids). Deletion reaches the client as a *position*: the in-band
    /// <c>cleared</c> SSE frame, and the refetch it triggers, which reads a database that
    /// already reflects the deletion. The returned count is UX only (the "Deleted N" toast).
    /// </para>
    /// </summary>
    int Clear(string? beforeIso, IReadOnlySet<long>? protectedSessionIds = null);

    /// <summary>
    /// #41 — atomically deletes one non-current session marker together with all of its
    /// request and FTS rows. The current marker is protected at execution time.
    /// </summary>
    SessionDeleteResult DeleteSession(long sessionId, IReadOnlySet<long>? protectedSessionIds = null);
}

public enum SessionDeleteStatus
{
    Deleted,
    NotFound,
    Current,
    InUse,
}

public sealed record SessionDeleteResult(SessionDeleteStatus Status, int Deleted);

/// <summary>
/// #29 — the outcome of resolving one per-request session name. <c>NameDropped</c> is true
/// when the name could not be given a marker of its own — it is over-length, or the
/// <see cref="SessionLimits.MaxMarkers"/> cap is reached — and the capture fell back to the
/// current session instead, so the writer can report the reattribution rather than applying
/// it silently.
/// </summary>
public sealed record NamedSessionResolution(SessionInfo Session, bool NameDropped);
