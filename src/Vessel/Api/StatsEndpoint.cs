using System.Text.Json;
using Vessel.Capture;
using Vessel.Storage;

namespace Vessel.Api;

/// <summary>D3 — <c>GET /stats?session={id|current|all}</c>, default <c>current</c>.</summary>
public static class StatsEndpoint
{
    public static async Task Handle(HttpContext context)
    {
        var store = context.RequestServices.GetRequiredService<SqliteReadStore>();
        var currentSession = context.RequestServices.GetRequiredService<CurrentSession>();

        string sessionParam = context.Request.Query.TryGetValue("session", out var raw) ? raw.ToString() : "current";

        long? sessionId = sessionParam switch
        {
            "all" => null,
            "current" or "" => currentSession.Id,
            _ when long.TryParse(sessionParam, out long parsed) => parsed,
            _ => currentSession.Id,
        };

        StatsResponse stats = store.GetStats(sessionId);
        context.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(
            context.Response.Body, stats, ApiJsonContext.Default.StatsResponse, context.RequestAborted);
    }
}
