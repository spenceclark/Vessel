using System.Text.Json;
using System.Text.Json.Nodes;
using Vessel.Capture;

namespace Vessel.Api;

/// <summary>
/// #49 — <c>PUT /vessel/api/requests/{id}/score</c>. A score is 1-5 on the request row, or
/// <c>null</c> to clear; there is no score history and no "who scored" — this is a
/// single-user local tool, so the latest value is the value. The write goes through the
/// capture writer like every other mutation rather than opening a second write connection.
/// <para>
/// No SSE frame is emitted: the client that scored already knows, and a second tab is stale
/// only until its own next refetch.
/// </para>
/// </summary>
// ponytail: no score event; add one alongside events.Cleared() (CaptureWriterService) if
// multi-tab scoring is ever a real complaint.
public static class ScoreEndpoint
{
    public const int MinScore = 1;
    public const int MaxScore = 5;

    public static async Task Handle(HttpContext context)
    {
        if (!long.TryParse(
                Convert.ToString(context.Request.RouteValues["id"], System.Globalization.CultureInfo.InvariantCulture),
                System.Globalization.CultureInfo.InvariantCulture,
                out long id)
            || id <= 0)
        {
            await VesselErrors.Write(
                context, StatusCodes.Status400BadRequest, VesselErrors.InvalidRequest,
                "request id must be a positive integer");
            return;
        }

        JsonNode? body;
        try
        {
            body = await JsonNode.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
        }
        catch (JsonException)
        {
            await VesselErrors.Write(
                context, StatusCodes.Status400BadRequest, VesselErrors.InvalidRequest,
                "score body must be valid JSON");
            return;
        }

        // Parsed as a node rather than a DTO so an *absent* member is distinguishable from an
        // explicit null: `{}` is a malformed request, `{"score":null}` deliberately clears.
        if (body is not JsonObject obj || !obj.TryGetPropertyValue("score", out JsonNode? node))
        {
            await VesselErrors.Write(
                context, StatusCodes.Status400BadRequest, VesselErrors.InvalidRequest,
                $"score must be an integer {MinScore}-{MaxScore}, or null to clear");
            return;
        }

        int? score = null;
        if (node is not null)
        {
            if (node.GetValueKind() != JsonValueKind.Number
                || !node.AsValue().TryGetValue(out int value)
                || value < MinScore || value > MaxScore)
            {
                await VesselErrors.Write(
                    context, StatusCodes.Status400BadRequest, VesselErrors.InvalidRequest,
                    $"score must be an integer {MinScore}-{MaxScore}, or null to clear");
                return;
            }

            score = value;
        }

        var channel = context.RequestServices.GetRequiredService<CaptureChannel>();
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.Enqueue(new SetScoreCommand(id, score, completion));

        bool updated;
        try
        {
            updated = await completion.Task.WaitAsync(context.RequestAborted);
        }
        catch (CaptureStoppedException ex)
        {
            await VesselErrors.Write(
                context, StatusCodes.Status503ServiceUnavailable, VesselErrors.CaptureStopped, ex.Message);
            return;
        }

        if (!updated)
        {
            // An in-flight request has no row yet, so it lands here naturally.
            await VesselErrors.Write(
                context, StatusCodes.Status404NotFound, VesselErrors.NotFound, $"no such request: {id}");
            return;
        }

        context.Response.StatusCode = StatusCodes.Status204NoContent;
    }
}
