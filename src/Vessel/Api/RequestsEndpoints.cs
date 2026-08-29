using System.Globalization;
using System.Text.Json;
using Vessel.Capture;
using Vessel.Config;
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

        string? q = NullIfEmpty(context.Request.Query["q"]);
        string? backend = NullIfEmpty(context.Request.Query["backend"]);
        string? model = NullIfEmpty(context.Request.Query["model"]);
        string? format = NullIfEmpty(context.Request.Query["format"]);
        string? tag = NullIfEmpty(context.Request.Query["tag"]);
        string? status = NullIfEmpty(context.Request.Query["status"]);
        bool warned = context.Request.Query["warned"] == "1";

        RequestListResponse response = store.ListRequests(
            limit, before, session, q, backend, model, format, tag, status, warned);
        context.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(
            context.Response.Body, response, ApiJsonContext.Default.RequestListResponse, context.RequestAborted);
    }

    /// <summary>
    /// D6 — <c>DELETE /requests?scope=all</c> or <c>?before=&lt;ISO-8601&gt;</c>. The delete
    /// runs on the writer thread (single-writer invariant); this handler never touches
    /// SQLite directly.
    /// </summary>
    public static async Task Delete(HttpContext context)
    {
        string? scope = NullIfEmpty(context.Request.Query["scope"]);
        string? beforeRaw = NullIfEmpty(context.Request.Query["before"]);

        string? beforeIso;
        if (scope == "all" && beforeRaw is null)
        {
            beforeIso = null;
        }
        else if (scope is null && beforeRaw is not null)
        {
            if (!DateTime.TryParse(
                    beforeRaw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime parsed))
            {
                await VesselErrors.Write(
                    context, StatusCodes.Status400BadRequest, VesselErrors.InvalidRequest,
                    $"'before' is not a valid ISO-8601 timestamp: {beforeRaw}");
                return;
            }

            beforeIso = parsed.ToUniversalTime().ToString("o");
        }
        else
        {
            await VesselErrors.Write(
                context, StatusCodes.Status400BadRequest, VesselErrors.InvalidRequest,
                "specify exactly one of ?scope=all or ?before=<ISO-8601>");
            return;
        }

        var channel = context.RequestServices.GetRequiredService<CaptureChannel>();
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.Enqueue(new ClearCommand(beforeIso, completion));

        int deleted;
        try
        {
            // R06 — bounded: the writer either runs this or fails it. Before, a give-up left
            // this awaiting a completion nobody would resolve, and HTTP cancellation didn't
            // bound the wait either.
            deleted = await completion.Task.WaitAsync(context.RequestAborted);
        }
        catch (CaptureStoppedException ex)
        {
            await VesselErrors.Write(
                context, StatusCodes.Status503ServiceUnavailable, VesselErrors.CaptureStopped, ex.Message);
            return;
        }

        context.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(
            context.Response.Body, new ClearResponse(deleted),
            ApiJsonContext.Default.ClearResponse, context.RequestAborted);
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    public static async Task Detail(HttpContext context)
    {
        if (!long.TryParse((string?)context.Request.RouteValues["id"], out long id))
        {
            await VesselErrors.Write(
                context, StatusCodes.Status404NotFound, VesselErrors.NotFound,
                $"no such request: {context.Request.RouteValues["id"]}");
            return;
        }

        var store = context.RequestServices.GetRequiredService<SqliteReadStore>();
        var configStore = context.RequestServices.GetRequiredService<ConfigStore>();

        // D01/R05: bodies are stored wire-true, so the display decode happens here, bounded
        // by the same capture budget the writer honours.
        RequestDetail? detail = store.GetDetail(id, CaptureBudget.MaxDecodedBytes(configStore.Current));
        if (detail is null)
        {
            await VesselErrors.Write(context, StatusCodes.Status404NotFound, VesselErrors.NotFound, $"no such request: {id}");
            return;
        }

        context.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(
            context.Response.Body, detail, ApiJsonContext.Default.RequestDetail, context.RequestAborted);
    }

    public static async Task Replays(HttpContext context)
    {
        if (!long.TryParse((string?)context.Request.RouteValues["id"], out long id))
        {
            await VesselErrors.Write(context, StatusCodes.Status404NotFound, VesselErrors.NotFound, "no such request");
            return;
        }

        var store = context.RequestServices.GetRequiredService<SqliteReadStore>();
        Summary[] rows = store.ListReplays(id);
        context.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(
            context.Response.Body, rows, ApiJsonContext.Default.SummaryArray, context.RequestAborted);
    }
}
