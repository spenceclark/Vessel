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
    string? PromptPreview);

/// <summary>One <c>get_request</c> body window, never an encoded binary payload.</summary>
public sealed record McpBodyWindow(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Text,
    long TotalChars,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Truncated,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Note,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Binary,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Bytes);

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
    bool Truncated);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(McpSearchResponse))]
[JsonSerializable(typeof(McpRequestResponse))]
[JsonSerializable(typeof(Vessel.Storage.StatsResponse))]
[JsonSerializable(typeof(Vessel.Storage.SessionInfo[]))]
public sealed partial class McpJsonContext : JsonSerializerContext;
