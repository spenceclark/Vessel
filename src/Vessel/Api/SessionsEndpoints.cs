using System.Text.Json;
using Vessel.Capture;
using Vessel.Storage;

namespace Vessel.Api;

/// <summary>D3/D4 — <c>GET /sessions</c> (newest-first list) and <c>POST /sessions</c> (reset: create + activate a marker).</summary>
public static class SessionsEndpoints
{
    public static async Task List(HttpContext context)
    {
        var store = context.RequestServices.GetRequiredService<SqliteReadStore>();
        SessionInfo[] sessions = store.ListSessions();
        context.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(
            context.Response.Body, sessions, ApiJsonContext.Default.SessionInfoArray, context.RequestAborted);
    }

    public static async Task Create(HttpContext context)
    {
        // No Content-Length gate: a client may omit the body entirely, send "{}", or use
        // chunked encoding — all of these should fall back to "no name" rather than fail
        // the reset, so just attempt the parse and swallow anything that isn't valid JSON.
        string? name = null;
        try
        {
            CreateSessionRequest? body = await JsonSerializer.DeserializeAsync(
                context.Request.Body, ApiJsonContext.Default.CreateSessionRequest, context.RequestAborted);
            name = body?.Name;
        }
        catch (JsonException)
        {
        }

        var channel = context.RequestServices.GetRequiredService<CaptureChannel>();
        var currentSession = context.RequestServices.GetRequiredService<CurrentSession>();

        // D4 — the insert runs on the writer thread; this handler never touches SQLite directly.
        var completion = new TaskCompletionSource<SessionInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.Enqueue(new CreateSessionCommand(name, completion));

        SessionInfo info;
        try
        {
            // R06 — see the clear endpoint: bounded by the writer's terminal state and by
            // client cancellation, never an unbounded await.
            info = await completion.Task.WaitAsync(context.RequestAborted);
        }
        catch (CaptureStoppedException ex)
        {
            await VesselErrors.Write(
                context, StatusCodes.Status503ServiceUnavailable, VesselErrors.CaptureStopped, ex.Message);
            return;
        }

        currentSession.Set(info.Id);

        context.Response.StatusCode = StatusCodes.Status201Created;
        context.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(context.Response.Body, info, ApiJsonContext.Default.SessionInfo, context.RequestAborted);
    }

    /// <summary>#41 — deletes one non-current session and its captured rows as a writer command.</summary>
    public static async Task Delete(HttpContext context)
    {
        if (!long.TryParse(
                Convert.ToString(context.Request.RouteValues["id"], System.Globalization.CultureInfo.InvariantCulture),
                System.Globalization.CultureInfo.InvariantCulture,
                out long sessionId)
            || sessionId <= 0)
        {
            await VesselErrors.Write(
                context, StatusCodes.Status400BadRequest, VesselErrors.InvalidRequest,
                "session id must be a positive integer");
            return;
        }

        var channel = context.RequestServices.GetRequiredService<CaptureChannel>();
        var completion = new TaskCompletionSource<SessionDeleteResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.Enqueue(new DeleteSessionCommand(sessionId, completion));

        SessionDeleteResult result;
        try
        {
            result = await completion.Task.WaitAsync(context.RequestAborted);
        }
        catch (CaptureStoppedException ex)
        {
            await VesselErrors.Write(
                context, StatusCodes.Status503ServiceUnavailable, VesselErrors.CaptureStopped, ex.Message);
            return;
        }

        if (result.Status == SessionDeleteStatus.NotFound)
        {
            await VesselErrors.Write(
                context, StatusCodes.Status404NotFound, VesselErrors.NotFound,
                $"session {sessionId} was not found");
            return;
        }

        if (result.Status == SessionDeleteStatus.Current)
        {
            await VesselErrors.Write(
                context, StatusCodes.Status409Conflict, VesselErrors.InvalidRequest,
                "the current session cannot be deleted");
            return;
        }

        context.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(
            context.Response.Body, new ClearResponse(result.Deleted),
            ApiJsonContext.Default.ClearResponse, context.RequestAborted);
    }
}
