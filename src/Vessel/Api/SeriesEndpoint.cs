using System.Text.Json;
using Vessel.Storage;

namespace Vessel.Api;

/// <summary>
/// Phase 7 D1 — <c>GET /vessel/api/series</c>: the context-growth data (#25). One point per
/// captured request (per request × tag when <c>groupBy=tag</c>) over the one canonical list
/// scope, so a chart can never drift from the list it mirrors. Unknown
/// <c>metric</c>/<c>groupBy</c> values are a 400 invalid_request — never a silent default.
/// <c>session</c> keeps <c>/requests</c>' lenient parsing (absent or <c>all</c> =
/// unscoped); there is no <c>current</c> alias here — that one is <c>/stats</c>-only.
/// </summary>
public static class SeriesEndpoint
{
    public static async Task Handle(HttpContext context)
    {
        string? metricRaw = NullIfEmpty(context.Request.Query["metric"]);
        SeriesMetric? metric = metricRaw switch
        {
            null => SeriesMetric.TokensIn, // the documented default
            "tokens_in" => SeriesMetric.TokensIn,
            "tokens_out" => SeriesMetric.TokensOut,
            "tokens_total" => SeriesMetric.TokensTotal,
            _ => null,
        };
        if (metric is null)
        {
            await VesselErrors.Write(
                context, StatusCodes.Status400BadRequest, VesselErrors.InvalidRequest,
                "'metric' must be tokens_in, tokens_out, or tokens_total");
            return;
        }

        string? groupByRaw = NullIfEmpty(context.Request.Query["groupBy"]);
        SeriesGroupBy? groupBy = groupByRaw switch
        {
            null => SeriesGroupBy.None, // the documented default
            "none" => SeriesGroupBy.None,
            "tag" => SeriesGroupBy.Tag,
            "model" => SeriesGroupBy.Model,
            "backend" => SeriesGroupBy.Backend,
            _ => null,
        };
        if (groupBy is null)
        {
            await VesselErrors.Write(
                context, StatusCodes.Status400BadRequest, VesselErrors.InvalidRequest,
                "'groupBy' must be none, tag, model, or backend");
            return;
        }

        SeriesResponse response = context.RequestServices.GetRequiredService<SqliteReadStore>()
            .GetSeries(new SeriesQuery(ParseListScope(context), metric.Value, groupBy.Value));
        context.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(
            context.Response.Body, response, ApiJsonContext.Default.SeriesResponse, context.RequestAborted);
    }

    /// <summary>
    /// The canonical list scope exactly as <c>/requests</c> parses it (this is
    /// <see cref="ExportEndpoint.ParseQuery"/> without its <c>requestFormat</c> twist —
    /// here the capture-format filter keeps its plain <c>format</c> name; the alias exists
    /// only on /export, where <c>format</c> means the file format). Shared verbatim with
    /// <see cref="AggregateEndpoint"/> so both chart endpoints carry the same scope.
    /// </summary>
    internal static RequestQuery ParseListScope(HttpContext context) => new(
        SessionId: context.Request.Query.TryGetValue("session", out var sessionRaw) && long.TryParse(sessionRaw, out long session)
            ? session
            : null,
        Q: NullIfEmpty(context.Request.Query["q"]),
        Backend: NullIfEmpty(context.Request.Query["backend"]),
        Model: NullIfEmpty(context.Request.Query["model"]),
        Format: NullIfEmpty(context.Request.Query["format"]),
        Tag: NullIfEmpty(context.Request.Query["tag"]),
        Status: NullIfEmpty(context.Request.Query["status"]),
        Warned: context.Request.Query["warned"] == "1");

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}