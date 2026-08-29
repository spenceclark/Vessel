using System.Text.Json;
using Vessel.Capture;

namespace Vessel.Api;

/// <summary>
/// R11/F2/J0 — <c>GET /vessel/api/active</c>: the recovery snapshot. The client cannot
/// decide from paginated history whether a live row is still running (a completion off the
/// loaded pages, filtered out, or for a since-cleared row is invisible there), so recovery
/// asks the server directly and adopts its answer wholesale — the in-flight set, together
/// with the log position that set is true as of, taken in one critical section. Deliberately
/// separate from <c>/status</c>: this changes on every request and is fetched on demand
/// during recovery, not polled.
/// </summary>
public static class ActiveRequestsEndpoint
{
    public static Task Handle(HttpContext context)
    {
        var events = context.RequestServices.GetRequiredService<CaptureEvents>();
        ActiveRequests active = events.GetActiveRequests();

        context.Response.ContentType = "application/json; charset=utf-8";
        return JsonSerializer.SerializeAsync(
            context.Response.Body,
            new ActiveRequestsPayload(active.ActiveSeqs, active.LogPosition, active.ServerRunId),
            ApiJsonContext.Default.ActiveRequestsPayload,
            context.RequestAborted);
    }
}

/// <summary>
/// Wire shape for <see cref="ActiveRequestsEndpoint"/> (mirrored in <c>types.ts</c>).
/// <paramref name="LogPosition"/> (J0) is the SSE publish id this active set is true as of:
/// the client discards every event it is holding at or below it — those are already reflected
/// in <paramref name="ActiveSeqs"/> and in the database the refetch reads — and replays only
/// what came after. <paramref name="ServerRunId"/> (H0b(1)) lets reconciliation reject a
/// snapshot from a different Vessel run rather than mis-comparing this process's seqs and
/// positions against another's.
/// </summary>
public sealed record ActiveRequestsPayload(long[] ActiveSeqs, long LogPosition, string ServerRunId);
