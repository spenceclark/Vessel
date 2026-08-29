using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Vessel.Capture;
using Vessel.Storage;

namespace Vessel.Mcp;

/// <summary>Phase 5b's complete, deliberately read-only MCP tool surface.</summary>
[McpServerToolType]
public sealed class McpTools
{
    private const int DefaultSearchLimit = 20;
    private const int MaxSearchLimit = 100;
    private const int DefaultMaxChars = 4_000;
    private const int MaxChars = 20_000;

    [McpServerTool(Name = "search_requests", ReadOnly = true)]
    [Description("Search captured requests using the same full-text and filter semantics as Vessel history. Returns 20 compact, body-free rows by default (maximum 100); use nextBefore as before to page older rows. promptPreview is only a short preview—call get_request for windowed text.")]
    public static CallToolResult SearchRequests(
        SqliteReadStore store,
        [Description("Words to find in flattened prompt or response text; FTS syntax is treated as literal text.")] string? query = null,
        [Description("Exact backend name, case-insensitive.")] string? backend = null,
        [Description("Exact model name.")] string? model = null,
        [Description("Exact captured tag.")] string? tag = null,
        [Description("Request outcome: ok or error.")] string? status = null,
        [Description("Exact captured format.")] string? format = null,
        [Description("Numeric session id.")] long? sessionId = null,
        [Description("Only requests carrying one or more warnings.")] bool warnedOnly = false,
        [Description("Rows to return; defaults to 20 and is capped at 100.")] int limit = DefaultSearchLimit,
        [Description("Return only ids below this cursor, from a previous nextBefore value.")] long? before = null)
    {
        int boundedLimit = Math.Clamp(limit, 1, MaxSearchLimit);
        RequestListResponse page = store.ListRequests(
            boundedLimit, before, sessionId, query, backend, model, format, tag, status, warnedOnly,
            includePreview: true);

        McpSearchRow[] rows = page.Rows.Select(summary => new McpSearchRow(
            summary.Id, summary.StartedAt, summary.Method, summary.Path, summary.Backend, summary.Model,
            summary.Tags, summary.StatusCode, summary.Error, summary.DurationMs, summary.TtftMs,
            summary.TokPerSec, summary.TokensIn, summary.TokensOut, summary.StopReason, summary.Warnings,
            summary.PromptPreview)).ToArray();

        return Json(new McpSearchResponse(rows, page.NextBefore), McpJsonContext.Default.McpSearchResponse);
    }

    [McpServerTool(Name = "get_request", ReadOnly = true)]
    [Description("Read one captured request. include=text (default) returns flattened prompt and response text recreated from stored bodies at read time; include=raw returns decoded wire bodies. Each body is windowed to 4,000 characters by default (maximum 20,000), with an in-payload note telling you which offset to request next. Binary is reported by byte count and is never inlined.")]
    public static CallToolResult GetRequest(
        SqliteReadStore store,
        [Description("Captured request id.")] long id,
        [Description("text (default) for flattened prompt/response, or raw for decoded wire bodies.")] string include = "text",
        [Description("Characters per prompt/response body window; defaults to 4000 and is capped at 20000.")] int maxChars = DefaultMaxChars,
        [Description("Zero-based character offset to read from each body.")] int offset = 0)
    {
        if (include is not "text" and not "raw")
        {
            return Error("include must be 'text' or 'raw'");
        }

        McpRequestData? request = store.GetMcpRequest(id);
        if (request is null)
        {
            return Error($"request {id} was not found");
        }

        int boundedMaxChars = Math.Clamp(maxChars, 1, MaxChars);
        int boundedOffset = Math.Max(offset, 0);
        McpBodyWindow? prompt = include == "text"
            ? WindowText(request.PromptText, boundedOffset, boundedMaxChars)
            : WindowRaw(request.RequestBody, boundedOffset, boundedMaxChars);
        McpBodyWindow? response = include == "text"
            ? WindowText(request.ResponseText, boundedOffset, boundedMaxChars)
            : WindowRaw(request.ResponseBody, boundedOffset, boundedMaxChars);

        var payload = new McpRequestResponse(
            Summary(request.Summary), prompt, response, include, boundedOffset, boundedMaxChars);
        return Json(payload, McpJsonContext.Default.McpRequestResponse);
    }

    [McpServerTool(Name = "get_stats", ReadOnly = true)]
    [Description("Get Vessel totals, failures, averages, token totals, and whether any totals are estimated. sessionId defaults to current; use all for all history or a numeric session id for one session.")]
    public static CallToolResult GetStats(
        SqliteReadStore store,
        CurrentSession currentSession,
        [Description("current (default), all, or a numeric session id.")] string? sessionId = "current")
    {
        long? scope = sessionId switch
        {
            null or "" or "current" => currentSession.Id,
            "all" => null,
            _ when long.TryParse(sessionId, out long id) => id,
            _ => currentSession.Id,
        };
        return Json(store.GetStats(scope), McpJsonContext.Default.StatsResponse);
    }

    [McpServerTool(Name = "list_sessions", ReadOnly = true)]
    [Description("List captured session markers newest first. Defaults to 20 rows; use a larger limit only when you need to inspect older sessions.")]
    public static CallToolResult ListSessions(
        SqliteReadStore store,
        [Description("Sessions to return; defaults to 20 and is capped at 100.")] int limit = DefaultSearchLimit)
    {
        int boundedLimit = Math.Clamp(limit, 1, MaxSearchLimit);
        SessionInfo[] sessions = store.ListSessions().Take(boundedLimit).ToArray();
        return Json(sessions, McpJsonContext.Default.SessionInfoArray);
    }

    private static McpRequestSummary Summary(Summary summary) => new(
        summary.Id, summary.StartedAt, summary.SessionId, summary.Backend, summary.Tags, summary.Method,
        summary.Path, summary.Format, summary.Model, summary.StatusCode, summary.Error, summary.Streamed,
        summary.DurationMs, summary.TtftMs, summary.TokPerSec, summary.TokensIn, summary.TokensOut,
        summary.TokensEstimated, summary.StopReason, summary.Warnings, summary.Truncated);

    private static McpBodyWindow? WindowText(string? text, int offset, int maxChars)
    {
        if (text is null)
        {
            return null;
        }

        int start = Math.Min(offset, text.Length);
        int length = Math.Min(maxChars, text.Length - start);
        int end = start + length;
        bool truncated = end < text.Length;
        return new McpBodyWindow(
            text.Substring(start, length), text.Length, truncated,
            truncated ? $"truncated at {end} of {text.Length} — call again with offset={end}" : null,
            Binary: false, Bytes: null);
    }

    private static McpBodyWindow? WindowRaw(McpBodyData? body, int offset, int maxChars)
    {
        if (body is null)
        {
            return null;
        }

        if (body.Binary is true)
        {
            return new McpBodyWindow(null, 0, false, null, Binary: true, Bytes: body.Bytes);
        }

        return WindowText(body.Text, offset, maxChars);
    }

    private static CallToolResult Json<T>(T payload, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) => new()
    {
        Content = [new TextContentBlock { Text = JsonSerializer.Serialize(payload, typeInfo) }],
    };

    private static CallToolResult Error(string message) => new()
    {
        Content = [new TextContentBlock { Text = message }],
        IsError = true,
    };
}
