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
    bool Truncated,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PromptPreview = null,
    /// <summary>#48 — the multi-replay fan this row belongs to; null on originals and pre-v4 replays.</summary>
    string? ReplayGroup = null,
    /// <summary>#48 — compact JSON of the merge patch this child was composed with, null when none.</summary>
    string? ReplayPatch = null,
    /// <summary>#49 — the human score, 1-5; null is unrated, which is the default for everything.</summary>
    int? Score = null);

/// <summary>D3 — <c>GET /requests</c> response: a page of rows plus the next cursor.</summary>
public sealed record RequestListResponse(Summary[] Rows, long? NextBefore);

/// <summary>
/// D3 — a body prepared for display: valid UTF-8 renders as <see cref="Text"/>, anything
/// else as <see cref="Base64"/>. Exactly one of the two is non-null; the other is omitted
/// from the wire JSON entirely.
/// <para>
/// D01/R05: storage keeps the original wire bytes, so any <c>Content-Encoding</c> is undone
/// here, at read time, under the capture budget. <see cref="DecodeTruncated"/> says the body
/// expanded past that budget and what's shown is a prefix — omitted from the JSON when
/// false, so the common case is unchanged.
/// </para>
/// </summary>
public sealed record BodyPayload(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Text,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Base64,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool DecodeTruncated = false,
    [property: JsonIgnore] bool DecodeFailed = false);

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
    BodyPayload? ResponseRaw,
    string? ReplayGroup = null,
    string? ReplayPatch = null,
    int? Score = null)
{
    public static RequestDetail From(
        Summary s, JsonNode? requestHeaders, JsonNode? responseHeaders,
        BodyPayload? requestBody, BodyPayload? responseBody, BodyPayload? responseRaw) => new(
        s.Id, s.StartedAt, s.SessionId, s.Backend, s.Tags, s.Method, s.Path, s.Format, s.Model, s.StatusCode,
        s.Error, s.Streamed, s.ReplayOf, s.DurationMs, s.TtftMs, s.VesselOverheadMs, s.TokPerSec, s.TokensIn,
        s.TokensOut, s.TokensCachedRead, s.TokensCachedWrite, s.TokensEstimated, s.StopReason, s.Warnings,
        s.Truncated, requestHeaders, responseHeaders, requestBody, responseBody, responseRaw,
        s.ReplayGroup, s.ReplayPatch, s.Score);
}

/// <summary>
/// D3 — <c>GET /stats</c> response. Session fields are null when scoped to "all". The
/// token totals are <c>SUM</c>s over the same scope (null-safe → 0, never null — an
/// empty session's total is genuinely zero, not "not measured"); <see cref="TokensEstimated"/>
/// is true iff any contributing row had estimated counts, so the UI can flag the whole
/// total as approximate rather than presenting a mixed exact/estimated sum as exact.
/// </summary>
public sealed record StatsResponse(
    long Total,
    long Failed,
    double? AvgDurationMs,
    double? AvgTokPerSec,
    double? AvgTtftMs,
    long? SessionId,
    string? SessionStartedAt,
    long TokensIn,
    long TokensOut,
    long TokensCachedRead,
    long TokensCachedWrite,
    bool TokensEstimated);

/// <summary>D3/D4 — a <c>sessions</c> marker row: <c>GET/POST /sessions</c> wire shape.</summary>
public sealed record SessionInfo(
    long Id,
    string StartedAt,
    string? Name,
    bool IsCurrent,
    long RequestCount,
    string? LastRequestAt);

/// <summary>#29 review bounds for the per-request session control surface and its polled listing.</summary>
public static class SessionLimits
{
    public const int MaxNameLength = 128;

    public const int MaxMarkers = 500;
}

/// <summary>
/// D2 — <c>GET /requests/facets</c> response: distinct values for the filter-bar
/// dropdowns, scoped like the list. No counts.
/// </summary>
public sealed record FacetsResponse(string[] Backends, string[] Models, string[] Tags, string[] Formats);

/// <summary>Phase 7 bounds for the chart read endpoints (phase-7-charts.md §D1/D2).</summary>
public static class ChartLimits
{
    /// <summary>
    /// Series cap: the §2.3 ramp size, and the readability ceiling. Past it the server
    /// ranks by total metric value and drops the remainder (never merges).
    /// </summary>
    public const int MaxSeries = 6;

    /// <summary>
    /// Newest-request cap for the series endpoint — counted by distinct request, not by
    /// fanned-out row, so a multi-tag request's extra rows never shrink the effective
    /// window. When the predicate matches more requests than this, the newest 5000 are
    /// returned and <c>truncated</c> is reported so the UI can disclose both numbers.
    /// </summary>
    public const int MaxPoints = 5000;

    /// <summary>
    /// Aggregate group cap, with <c>totalGroups</c> reported. No "(other)" rollup:
    /// combining averages of averages is arithmetically wrong.
    /// </summary>
    public const int MaxGroups = 50;
}

/// <summary>Phase 7 — the metric a series chart draws.</summary>
public enum SeriesMetric
{
    TokensIn,
    TokensOut,
    TokensTotal,
}

/// <summary>Phase 7 — how a series chart splits points into series.</summary>
public enum SeriesGroupBy
{
    None,
    Tag,
    Model,
    Backend,
}

/// <summary>Phase 7 — the dimension an aggregate report groups by.</summary>
/// <summary>
/// #49 review — what the <see cref="ChartLimits.MaxGroups"/> cap keeps. The cap is applied
/// after ranking, so a leaderboard ranked client-side out of a token-ranked page is not the
/// scope's leaderboard: 50 chatty models scoring 1/5 would hide the one quiet model scoring
/// 5/5. <see cref="Score"/> also drops unscored groups — an unrated group is not last place,
/// it is absent — so <c>totalGroups</c> is the size of the ranked population either way.
/// </summary>
public enum AggregateRank
{
    Tokens,
    Score,
}

public enum AggregateDimension
{
    Model,
    Tag,
    Backend,
    Format,

    /// <summary>
    /// #49 — one row per distinct replay patch (`replay_patch`), NULL excluded: the
    /// per-parameter-set leaderboard, grouping every temp-0.2 replay across every prompt.
    /// This is why #48 stored the applied patch as a column instead of re-deriving it.
    /// </summary>
    Patch,

    /// <summary>
    /// #25/#26 live-use feedback — one row per warning code present in scope. Fans out
    /// like <see cref="Tag"/> (a request carrying several warnings counts once per code);
    /// human labels come from <c>lib/warnings.ts</c>' existing map, not this API.
    /// </summary>
    Warning,
}

/// <summary>
/// #25/#26 live-use feedback — which JSON-array column <see cref="SqliteReadStore"/>'s
/// shared filtered-query builder fans out over (one row per array element), if any.
/// Generalizes the tag-only fan-out the phase-7 spec shipped with, so a warning-code
/// breakdown reuses the identical join mechanism instead of a second copy of it.
/// </summary>
public enum FanOutColumn
{
    None,
    Tags,
    Warnings,
}

/// <summary>
/// Phase 7 — deterministic series/group key order: the null key ("(none)") first, then
/// ordinal ascending — the same order SQLite's default ASC yields, so rank tiebreaks in
/// code resolve the way the database would have ordered them and charts are stable across
/// refetches.
/// </summary>
public sealed class SeriesKeyOrder : IComparer<string?>
{
    public static readonly SeriesKeyOrder Instance = new();

    public int Compare(string? x, string? y)
    {
        if (x is null && y is null) return 0;
        if (x is null) return -1;
        if (y is null) return 1;
        return string.CompareOrdinal(x, y);
    }
}

/// <summary>
/// Phase 7 D1 — the series query: the one canonical list scope (<see cref="RequestQuery"/>)
/// plus metric and grouping. Session scoping keeps <c>/requests</c> semantics (an absent or
/// <c>all</c> scope is unscoped; there is no <c>current</c> alias here — that is
/// <c>/stats</c>-only).
/// </summary>
public sealed record SeriesQuery(RequestQuery Scope, SeriesMetric Metric, SeriesGroupBy GroupBy);

/// <summary>Phase 7 D2 — the aggregate query: the canonical list scope plus the grouping dimension.</summary>
public sealed record AggregateQuery(RequestQuery Scope, AggregateDimension By, AggregateRank Rank = AggregateRank.Tokens);

/// <summary>
/// Phase 7 D1 — one chart point: the request's id (so a click can select it), its ISO
/// <c>started_at</c>, and the metric value.
/// </summary>
public sealed record SeriesPoint(long Id, string T, long V);

/// <summary>
/// Phase 7 D1 — one named series. <paramref name="Key"/> is <c>null</c> when the grouping
/// column had no value (an untagged request, or a capture with no model); the UI renders
/// it <c>(none)</c>.
/// </summary>
public sealed record SeriesGroup(string? Key, SeriesPoint[] Points);

/// <summary>
/// Phase 7 D1 — <c>GET /vessel/api/series</c> response. Points are oldest-first by id
/// (insertion order is the true chronology; <c>started_at</c> can tie or skew).
/// <see cref="Returned"/> and <see cref="TotalMatching"/> both count distinct requests,
/// not rows — a request fanned out across several tag series is one request, so the two
/// numbers reconcile the way the truncation disclosure states them ("most recent N of M
/// requests"). <see cref="TotalMatching"/> is computed only when the point cap was hit;
/// null-metric rows are excluded by predicate, so the counts and the returned points
/// reconcile exactly. <see cref="OmittedSeries"/> counts series dropped (not merged) past
/// <see cref="ChartLimits.MaxSeries"/>. <see cref="Estimated"/> is true when any
/// contributing row had estimated token counts — the whole chart is approximate.
/// </summary>
public sealed record SeriesResponse(
    string Metric,
    string GroupBy,
    SeriesGroup[] Series,
    int Returned,
    long TotalMatching,
    bool Truncated,
    int OmittedSeries,
    bool Estimated);

/// <summary>
/// Phase 7 D2 — one aggregate row. <see cref="Failed"/> uses the stats predicate verbatim
/// (<c>error IS NOT NULL OR status_code &gt;= 400</c>); <see cref="AvgTtftMs"/> averages
/// streamed rows only (mirrors <c>GetStats</c>); every average ignores nulls and every sum
/// is <c>COALESCE(…, 0)</c>. <see cref="TokensEstimated"/> is <c>MAX(tokens_estimated)</c>
/// per group. For a fanned-out dimension (<c>tag</c>, <c>warning</c>) a multi-valued request
/// is counted once per value, so rows can sum past the session total (disclosed in the UI).
/// <see cref="P50DurationMs"/>/<see cref="P95DurationMs"/> (#26 live-use feedback) are
/// nearest-rank percentiles over the group's non-null durations — computed for every
/// dimension uniformly (the same per-group data is already being read), not just where a
/// card currently shows them; both are null for a group with no measured duration.
/// </summary>
public sealed record AggregateRow(
    string? Key,
    long Requests,
    long Failed,
    long TokensIn,
    long TokensOut,
    long TokensCachedRead,
    long TokensCachedWrite,
    double? AvgDurationMs,
    double? AvgTtftMs,
    double? AvgTokPerSec,
    bool TokensEstimated,
    double? P50DurationMs,
    double? P95DurationMs,
    /// <summary>#49 — mean of the group's non-null scores, null when nothing in it is scored.</summary>
    double? MeanScore = null,
    /// <summary>#49 — how many of the group's requests carry a score.</summary>
    long Scored = 0,
    /// <summary>
    /// #49 — replay-group wins: groups in which this key holds the top score (ties are wins
    /// for every key at the top), out of the groups it has a scored member in. Null for
    /// dimensions where a fan comparison is meaningless — only model and patch have one.
    /// </summary>
    long? Wins = null,
    long? Groups = null);

/// <summary>
/// Phase 7 D2 — <c>GET /vessel/api/aggregate</c> response: at most
/// <see cref="ChartLimits.MaxGroups"/> rows (sorted by tokens in+out desc, then requests
/// desc, then key asc) plus the untruncated group count.
/// </summary>
public sealed record AggregateResponse(string By, AggregateRow[] Rows, long TotalGroups);
