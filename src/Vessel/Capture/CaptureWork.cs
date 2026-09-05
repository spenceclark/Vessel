using Vessel.Storage;

namespace Vessel.Capture;

/// <summary>
/// Union of what flows through the writer's channel (D4): captured requests, the
/// common case, and control commands that must run on the single writer thread — a
/// session reset needs a writer-thread-safe insert without a second write connection.
/// </summary>
public abstract record CaptureWork;

public sealed record CapturedRequest(CaptureRecord Record) : CaptureWork;

/// <summary>
/// D4 — <c>POST /sessions</c> enqueues this instead of writing to SQLite itself; the
/// writer executes the insert and completes <see cref="Completion"/> with the new row so
/// the API handler can respond (and update <see cref="CurrentSession"/>) without a second
/// write connection or a lock dance.
/// </summary>
public sealed record CreateSessionCommand(string? Name, TaskCompletionSource<SessionInfo> Completion) : CaptureWork;

/// <summary>
/// D6 — <c>DELETE /requests</c> enqueues this: <see cref="BeforeIso"/> null means "clear
/// all", otherwise an ISO-8601 UTC timestamp ("clear before"). Runs on the writer thread,
/// like <see cref="CreateSessionCommand"/>; <see cref="Completion"/> carries the number of
/// <c>requests</c> rows deleted (UX only — R23/H0a: the client purges on the in-band
/// <c>cleared</c> SSE event, not on a boundary in this ack).
/// </summary>
public sealed record ClearCommand(string? BeforeIso, TaskCompletionSource<int> Completion) : CaptureWork;

/// <summary>
/// #41 — scoped clear for one session. Rows, FTS entries, and the non-current marker are
/// deleted together on the writer thread.
/// </summary>
public sealed record DeleteSessionCommand(
    long SessionId,
    TaskCompletionSource<SessionDeleteResult> Completion) : CaptureWork;

/// <summary>
/// #49 — <c>PUT /requests/{id}/score</c> enqueues this rather than opening a second write
/// connection, like every other mutation. <see cref="Completion"/> carries false when no row
/// matched, which the endpoint turns into a 404.
/// </summary>
public sealed record SetScoreCommand(
    long Id,
    int? Score,
    TaskCompletionSource<bool> Completion) : CaptureWork;
