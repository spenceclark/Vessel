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
            new ActiveRequestsPayload(active.Active, active.LogPosition, active.ServerRunId),
            ApiJsonContext.Default.ActiveRequestsPayload,
            context.RequestAborted);
    }
}

/// <summary>
/// Wire shape for <see cref="ActiveRequestsEndpoint"/> (mirrored in <c>types.ts</c>).
/// <paramref name="Active"/> (K0b) is the in-flight requests in seq order, each carrying the
/// metadata its <c>started</c> frame carried, so the client can *render* what it is told is
/// running even when that frame was the one its bounded queue dropped.
/// <paramref name="LogPosition"/> (J0) is the SSE publish id this set is true as of: the client
/// discards every event it is holding at or below it — those are already reflected in
/// <paramref name="Active"/> and in the database the refetch reads — and replays only what came
/// after. <paramref name="ServerRunId"/> (H0b(1)) lets recovery reject a snapshot from a
/// different Vessel run rather than mis-comparing this process's seqs and positions against
/// another's.
/// </summary>
public sealed record ActiveRequestsPayload(
    ActiveDescriptor[] Active, long LogPosition, string ServerRunId);
