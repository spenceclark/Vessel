using System.Text.Json;
using Vessel.Storage;

namespace Vessel.Api;

/// <summary>D2 — <c>GET /requests/facets?session=</c>: distinct backend/model/tag/format values for the filter bar.</summary>
public static class FacetsEndpoint
{
    public static async Task Handle(HttpContext context)
    {
        var store = context.RequestServices.GetRequiredService<SqliteReadStore>();

        long? sessionId = context.Request.Query.TryGetValue("session", out var sessionRaw) && long.TryParse(sessionRaw, out long parsedSession)
            ? parsedSession
            : null;

        FacetsResponse facets = store.GetFacets(sessionId);
        context.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(
            context.Response.Body, facets, ApiJsonContext.Default.FacetsResponse, context.RequestAborted);
    }
}
