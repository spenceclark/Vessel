using System.Text.Json.Serialization;

namespace Vessel.Mcp;

/// <summary>Compact text-content payload for <c>search_requests</c>.</summary>
public sealed record McpSearchResponse(McpSearchRow[] Rows, long? NextBefore);

/// <summary>The deliberately body-free search row exposed to MCP clients.</summary>
public sealed record McpSearchRow(
    long Id,
    string StartedAt,
    string Method,
    string Path,
    string Backend,
    string? Model,
    string[] Tags,
    int? StatusCode,
    string? Error,
    double? DurationMs,
    double? TtftMs,
    double? TokPerSec,
    long? TokensIn,
    long? TokensOut,
    string? StopReason,
    string[] Warnings,
    string? PromptPreview,
    /// <summary>#49 — the human score, so an agent can read which variant a person preferred.</summary>
    int? Score,
    /// <summary>#48 — the replay links, so a fan can be identified from MCP alone.</summary>
    long? ReplayOf,
    string? ReplayGroup,
    string? ReplayPatch);

/// <summary>One <c>get_request</c> body window, never an encoded binary payload.</summary>
public sealed record McpBodyWindow(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Text,
    long TotalChars,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Truncated,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Note,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Binary,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Bytes,
    /// <summary>#94 — the body exceeded <c>capture.maxBodyMb</c> once decoded, so text covers only its start.</summary>
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool DecodeTruncated = false);

/// <summary>Summary plus two bounded bodies for <c>get_request</c>.</summary>
public sealed record McpRequestResponse(
    McpRequestSummary Summary,
    McpBodyWindow? Prompt,
    McpBodyWindow? Response,
    string Include,
    int Offset,
    int MaxChars);

/// <summary>The request summary repeated by <c>get_request</c> without headers or bodies.</summary>
public sealed record McpRequestSummary(
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
    double? DurationMs,
    double? TtftMs,
    double? TokPerSec,
    long? TokensIn,
    long? TokensOut,
    bool TokensEstimated,
    string? StopReason,
    string[] Warnings,
    bool Truncated,
    int? Score,
    long? ReplayOf,
    string? ReplayGroup,
    string? ReplayPatch);

/// <summary>#87 — the <c>vessel://sessions/{id}</c> resource: marker, totals, and most recent requests.</summary>
public sealed record McpSessionResource(
    Vessel.Storage.SessionInfo Session,
    Vessel.Storage.StatsResponse Stats,
    McpSearchRow[] RecentRequests);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(McpSearchResponse))]
[JsonSerializable(typeof(McpRequestResponse))]
[JsonSerializable(typeof(Vessel.Storage.StatsResponse))]
[JsonSerializable(typeof(Vessel.Storage.SessionInfo[]))]
[JsonSerializable(typeof(McpSessionResource))]
public sealed partial class McpJsonContext : JsonSerializerContext;
