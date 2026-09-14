using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Vessel.Storage;

namespace Vessel.Mcp;

/// <summary>
/// #87 — captured requests and sessions as read-only MCP resources, so a client can attach
/// one by mention instead of the model calling a tool. Reads reuse the tool code paths.
/// Attached content can't page, so each body is one window of <see cref="McpTools.MaxChars"/>;
/// a truncated body's note still points at <c>get_request</c> for the rest.
/// </summary>
[McpServerResourceType]
public sealed class McpResources
{
    private const string JsonMime = "application/json";
    private const int ListedRequests = 20;
    private const int ListedSessions = 10;
    private const int SessionRecentRequests = 20;

    [McpServerResource(UriTemplate = "vessel://requests/{id}", Name = "request", MimeType = JsonMime)]
    [Description("One captured request: summary plus flattened prompt and response text, each capped at 20,000 characters.")]
    public static string Request(SqliteReadStore store, long id) =>
        McpTools.ReadRequest(store, id, "text", McpTools.MaxChars, 0) is McpRequestResponse payload
            ? JsonSerializer.Serialize(payload, McpJsonContext.Default.McpRequestResponse)
            : throw NotFound($"vessel://requests/{id}");

    [McpServerResource(UriTemplate = "vessel://sessions/{id}", Name = "session", MimeType = JsonMime)]
    [Description("One session marker with its totals and its 20 most recent requests.")]
    public static string Session(SqliteReadStore store, long id)
    {
        SessionInfo session = store.GetSession(id) ?? throw NotFound($"vessel://sessions/{id}");
        McpSearchRow[] recent = store.ListRequests(SessionRecentRequests, before: null, id, includePreview: true)
            .Rows.Select(McpTools.SearchRow).ToArray();
        return JsonSerializer.Serialize(
            new McpSessionResource(session, store.GetStats(id), recent), McpJsonContext.Default.McpSessionResource);
    }

    /// <summary>
    /// <c>resources/list</c>: the most recent requests and sessions as concrete, mentionable
    /// resources. Older ones stay reachable through the URI templates.
    /// </summary>
    public static ValueTask<ListResourcesResult> List(
        RequestContext<ListResourcesRequestParams> context, CancellationToken cancellationToken)
    {
        SqliteReadStore store = context.Services!.GetRequiredService<SqliteReadStore>();

        IEnumerable<Resource> requests = store.ListRequests(ListedRequests, before: null, sessionId: null).Rows
            .Select(r => new Resource
            {
                Uri = $"vessel://requests/{r.Id}",
                Name = $"request {r.Id}",
                Title = $"#{r.Id} {r.Method} {r.Path}{(r.Model is null ? "" : $" · {r.Model}")}",
                MimeType = JsonMime,
            });
        IEnumerable<Resource> sessions = store.ListSessions().Take(ListedSessions)
            .Select(s => new Resource
            {
                Uri = $"vessel://sessions/{s.Id}",
                Name = $"session {s.Id}",
                Title = $"Session {s.Id}{(s.Name is null ? "" : $" · {s.Name}")} ({s.RequestCount} requests)",
                MimeType = JsonMime,
            });

        return ValueTask.FromResult(new ListResourcesResult { Resources = [.. requests, .. sessions] });
    }

    private static McpProtocolException NotFound(string uri) =>
        new($"resource {uri} was not found", McpErrorCode.ResourceNotFound);
}
