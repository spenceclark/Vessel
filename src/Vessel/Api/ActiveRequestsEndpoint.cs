using System.Text.Json;
using Vessel.Capture;

namespace Vessel.Api;

/// <summary>
/// R11/F2 — <c>GET /vessel/api/active</c>: the server-authoritative in-flight set. The
/// client cannot decide from paginated history whether a live row is still running (a
/// completion off the loaded pages, filtered out, or for a since-cleared row is invisible
/// there), so reconciliation asks the server directly and removes any in-flight row the
/// server no longer lists as active. Deliberately separate from <c>/status</c>: this
/// changes on every request and is fetched on demand during reconciliation, not polled.
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
            new ActiveRequestsPayload(active.ActiveSeqs, active.NewestCompletedSeq),
            ApiJsonContext.Default.ActiveRequestsPayload,
            context.RequestAborted);
    }
}

/// <summary>Wire shape for <see cref="ActiveRequestsEndpoint"/> (mirrored in <c>types.ts</c>).</summary>
public sealed record ActiveRequestsPayload(long[] ActiveSeqs, long NewestCompletedSeq);
