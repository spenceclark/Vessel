using System.Text.Json;
using Vessel.Storage;

namespace Vessel.Api;

/// <summary>
/// Phase 7 D2 — <c>GET /vessel/api/aggregate</c>: every #26 report from one endpoint —
/// "keep the query set small and mechanical" — over the same canonical list scope as
/// <see cref="SeriesEndpoint"/>. <c>by</c> has no default: an absent or unknown dimension
/// is a 400 invalid_request, never a silent guess.
/// </summary>
public static class AggregateEndpoint
{
    public static async Task Handle(HttpContext context)
    {
        string? byRaw = NullIfEmpty(context.Request.Query["by"]);
        AggregateDimension? by = byRaw switch
        {
            "model" => AggregateDimension.Model,
            "tag" => AggregateDimension.Tag,
            "backend" => AggregateDimension.Backend,
            "format" => AggregateDimension.Format,
            "patch" => AggregateDimension.Patch,
            "warning" => AggregateDimension.Warning,
            _ => null,
        };
        if (by is null)
        {
            await VesselErrors.Write(
                context, StatusCodes.Status400BadRequest, VesselErrors.InvalidRequest,
                "'by' must be model, tag, backend, format, patch, or warning");
            return;
        }

        string? rankRaw = NullIfEmpty(context.Request.Query["rank"]);
        AggregateRank? rank = rankRaw switch
        {
            null or "tokens" => AggregateRank.Tokens,
            "score" => AggregateRank.Score,
            _ => null,
        };
        if (rank is null)
        {
            await VesselErrors.Write(
                context, StatusCodes.Status400BadRequest, VesselErrors.InvalidRequest,
                "'rank' must be tokens or score");
            return;
        }

        AggregateResponse response = context.RequestServices.GetRequiredService<SqliteReadStore>()
            .GetAggregate(new AggregateQuery(SeriesEndpoint.ParseListScope(context), by.Value, rank.Value));
        context.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(
            context.Response.Body, response, ApiJsonContext.Default.AggregateResponse, context.RequestAborted);
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}