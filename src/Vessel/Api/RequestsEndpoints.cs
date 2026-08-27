using System.Text.Json;
using Vessel.Storage;

namespace Vessel.Api;

/// <summary>D3 — <c>GET /requests</c> (paged list) and <c>GET /requests/{id}</c> (full detail).</summary>
public static class RequestsEndpoints
{
    private const int DefaultLimit = 100;
    private const int MaxLimit = 500;

    public static async Task List(HttpContext context)
    {
        var store = context.RequestServices.GetRequiredService<SqliteReadStore>();

        int limit = DefaultLimit;
        if (context.Request.Query.TryGetValue("limit", out var limitRaw) && int.TryParse(limitRaw, out int parsedLimit))
        {
            limit = parsedLimit;
        }

        limit = Math.Clamp(limit, 1, MaxLimit);

        long? before = context.Request.Query.TryGetValue("before", out var beforeRaw) && long.TryParse(beforeRaw, out long parsedBefore)
            ? parsedBefore
            : null;

        long? session = context.Request.Query.TryGetValue("session", out var sessionRaw) && long.TryParse(sessionRaw, out long parsedSession)
            ? parsedSession
            : null;

        RequestListResponse response = store.ListRequests(limit, before, session);
        context.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(
            context.Response.Body, response, ApiJsonContext.Default.RequestListResponse, context.RequestAborted);
    }

    public static async Task Detail(HttpContext context)
    {
        long id = long.Parse((string)context.Request.RouteValues["id"]!);
        var store = context.RequestServices.GetRequiredService<SqliteReadStore>();

        RequestDetail? detail = store.GetDetail(id);
        if (detail is null)
        {
            await VesselErrors.Write(context, StatusCodes.Status404NotFound, VesselErrors.NotFound, $"no such request: {id}");
            return;
        }

        context.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(
            context.Response.Body, detail, ApiJsonContext.Default.RequestDetail, context.RequestAborted);
    }
}
