using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Vessel.Storage;

/// <summary>
/// D3 — the <c>requests</c> row shape returned by the list and SSE <c>completed</c> event:
/// every column except the header/body blobs. Property names map to camelCase on the wire
/// (STJ source-gen, <see cref="Vessel.Api.ApiJsonContext"/>).
/// </summary>
public sealed record Summary(
    long Id,
    string StartedAt,
    long? SessionId,
    string Backend,
    string[] Tags,
    string Method,
    string Path,
    string Format,
    string? Model,
    int? StatusCode,
    string? Error,
    bool Streamed,
    long? ReplayOf,
    double? DurationMs,
    double? TtftMs,
    double? VesselOverheadMs,
    double? TokPerSec,
    long? TokensIn,
    long? TokensOut,
    long? TokensCachedRead,
    long? TokensCachedWrite,
    bool TokensEstimated,
    string? StopReason,
    string[] Warnings,
    bool Truncated);

/// <summary>D3 — <c>GET /requests</c> response: a page of rows plus the next cursor.</summary>
public sealed record RequestListResponse(Summary[] Rows, long? NextBefore);

/// <summary>
/// D3 — a decompressed body: valid UTF-8 renders as <see cref="Text"/>, anything else as
/// <see cref="Base64"/>. Exactly one of the two is non-null; the other is omitted from
/// the wire JSON entirely.
/// </summary>
public sealed record BodyPayload(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Text,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Base64);

/// <summary>
/// D3 — <c>GET /requests/{id}</c> response: every <see cref="Summary"/> field flattened to
/// the top level (not nested — the wire shape is one flat object) plus headers and bodies.
/// </summary>
public sealed record RequestDetail(
    long Id,
    string StartedAt,
    long? SessionId,
    string Backend,
    string[] Tags,
    string Method,
    string Path,
    string Format,
    string? Model,
    int? StatusCode,
    string? Error,
    bool Streamed,
    long? ReplayOf,
    double? DurationMs,
    double? TtftMs,
    double? VesselOverheadMs,
    double? TokPerSec,
    long? TokensIn,
    long? TokensOut,
    long? TokensCachedRead,
    long? TokensCachedWrite,
    bool TokensEstimated,
    string? StopReason,
    string[] Warnings,
    bool Truncated,
    JsonNode? RequestHeaders,
    JsonNode? ResponseHeaders,
    BodyPayload? RequestBody,
    BodyPayload? ResponseBody,
    BodyPayload? ResponseRaw)
{
    public static RequestDetail From(
        Summary s, JsonNode? requestHeaders, JsonNode? responseHeaders,
        BodyPayload? requestBody, BodyPayload? responseBody, BodyPayload? responseRaw) => new(
        s.Id, s.StartedAt, s.SessionId, s.Backend, s.Tags, s.Method, s.Path, s.Format, s.Model, s.StatusCode,
        s.Error, s.Streamed, s.ReplayOf, s.DurationMs, s.TtftMs, s.VesselOverheadMs, s.TokPerSec, s.TokensIn,
        s.TokensOut, s.TokensCachedRead, s.TokensCachedWrite, s.TokensEstimated, s.StopReason, s.Warnings,
        s.Truncated, requestHeaders, responseHeaders, requestBody, responseBody, responseRaw);
}

/// <summary>D3 — <c>GET /stats</c> response. Session fields are null when scoped to "all".</summary>
public sealed record StatsResponse(
    long Total,
    long Failed,
    double? AvgDurationMs,
    double? AvgTokPerSec,
    double? AvgTtftMs,
    long? SessionId,
    string? SessionStartedAt);

/// <summary>D3/D4 — a <c>sessions</c> marker row: <c>GET/POST /sessions</c> wire shape.</summary>
public sealed record SessionInfo(long Id, string StartedAt, string? Name);
