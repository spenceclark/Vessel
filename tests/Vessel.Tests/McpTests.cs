using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Vessel.Api;
using Vessel.Config;
using Vessel.Storage;
using Xunit;

namespace Vessel.Tests;

/// <summary>Phase 5b M1–M6: the SDK client exercises the mounted Streamable HTTP server.</summary>
public sealed class McpTests
{
    private static CancellationToken CT => TestContext.Current.CancellationToken;

    [Fact]
    public async Task M1_SdkClient_ListsExactlyFourDescribedTools()
    {
        await using TestVessel vessel = await TestVessel.StartAsync();
        await using McpClient client = await Connect(vessel.BaseUrl);

        IList<McpClientTool> tools = await client.ListToolsAsync(cancellationToken: CT);

        Assert.Equal(
            ["get_request", "get_stats", "list_sessions", "search_requests"],
            tools.Select(tool => tool.Name).OrderBy(name => name));
        Assert.All(tools, tool =>
        {
            Assert.False(string.IsNullOrWhiteSpace(tool.Description));
            Assert.Equal("object", tool.ProtocolTool.InputSchema.GetProperty("type").GetString());
        });
    }

    [Fact]
    public async Task M2_SearchRequests_MatchesRestFilters_PagesAndCaps()
    {
        await using TestVessel vessel = await TestVessel.StartAsync();
        long sessionId = await CreateSession(vessel.BaseUrl, "MCP parity");
        long alpha = Seed(vessel.DbPath, sessionId, "alpha", "model-a", ["planner"], 200, null, null, "needle alpha", "answer alpha");
        long beta = Seed(vessel.DbPath, sessionId, "beta", "model-b", ["runner"], 500, null, "http_error", "needle beta", "answer beta");
        Seed(vessel.DbPath, null, "alpha", "model-c", ["other"], 200, null, null, "unrelated", "elsewhere");
        for (int i = 0; i < 101; i++)
        {
            Seed(vessel.DbPath, null, "stub", "cap-model", [], 200, null, null, $"cap {i}", "cap");
        }

        await using McpClient client = await Connect(vessel.BaseUrl);
        var filters = new Dictionary<string, string?>
        {
            ["query"] = "needle",
            ["backend"] = "ALPHA",
            ["model"] = "model-a",
            ["tag"] = "planner",
            ["status"] = "ok",
            ["format"] = "openai-chat",
            ["sessionId"] = sessionId.ToString(),
            ["warnedOnly"] = "false",
        };

        foreach ((string key, string? value) in filters)
        {
            if (value is null)
            {
                continue;
            }

            long[] restIds = await RestIds(vessel.BaseUrl, ToRestQuery(key, value));
            long[] mcpIds = await SearchIds(client, new Dictionary<string, object?> { [key] = ValueForTool(key, value) });
            Assert.Equal(restIds, mcpIds);
        }

        // Combined filters retain the same AND semantics as REST.
        Assert.Equal([alpha], await SearchIds(client, new Dictionary<string, object?>
        {
            ["query"] = "needle", ["backend"] = "alpha", ["tag"] = "planner", ["sessionId"] = sessionId,
        }));
        Assert.Equal([beta], await SearchIds(client, new Dictionary<string, object?> { ["status"] = "error" }));

        // FTS control characters/operators stay literal and never turn into a protocol error.
        CallToolResult hostile = await client.CallToolAsync(
            "search_requests", new Dictionary<string, object?> { ["query"] = "AND ( * \"" }, cancellationToken: CT);
        Assert.False(hostile.IsError is true);

        McpSearchPayload capped = await Search(client, new Dictionary<string, object?> { ["limit"] = 9999 });
        Assert.Equal(100, capped.Rows.Length);
        Assert.NotNull(capped.NextBefore);
        McpSearchPayload first = await Search(client, new Dictionary<string, object?> { ["limit"] = 1 });
        Assert.NotNull(first.NextBefore);
        McpSearchPayload second = await Search(client, new Dictionary<string, object?> { ["limit"] = 100, ["before"] = first.NextBefore });
        Assert.DoesNotContain(first.Rows[0].Id, second.Rows.Select(row => row.Id));
        Assert.All(second.Rows, row => Assert.True(row.Id < first.Rows[0].Id));
    }

    // phase-5-mcp.md §7: search_requests must read promptPreview from the stored column,
    // never decode a row's body. Proven with a corruption canary — rows whose request_body
    // is garbage (not valid zstd) would make BodyCompression.Decompress throw the instant
    // anything tried to decode it, so a search across 100+ such rows succeeding at all,
    // returning the exact stored previews, is only possible via a column select.
    [Fact]
    public async Task M2b_SearchRequests_ReadsPreviewColumn_NeverDecodesBody()
    {
        await using TestVessel vessel = await TestVessel.StartAsync();
        long[] ids = new long[105];
        for (int i = 0; i < ids.Length; i++)
        {
            ids[i] = SeedWithPoisonedBodyAndPreview(vessel.DbPath, $"preview-{i}", $"response-{i}");
        }

        await using McpClient client = await Connect(vessel.BaseUrl);
        McpSearchPayload page = await Search(client, new Dictionary<string, object?> { ["limit"] = 100 });
        Assert.Equal(100, page.Rows.Length);
        Assert.All(page.Rows, row => Assert.NotNull(row.PromptPreview));
        Assert.Equal($"preview-{ids.Length - 1}", page.Rows[0].PromptPreview);

        // A row with a NULL preview (pre-migration shape) simply omits the field.
        long nullPreviewId = SeedWithPoisonedBodyAndPreview(vessel.DbPath, null, null);
        McpSearchPayload afterNull = await Search(client, new Dictionary<string, object?> { ["limit"] = 1, ["before"] = nullPreviewId + 1 });
        Assert.Equal(nullPreviewId, afterNull.Rows[0].Id);
        Assert.Null(afterNull.Rows[0].PromptPreview);
    }

    private static long SeedWithPoisonedBodyAndPreview(string dbPath, string? promptPreview, string? responsePreview)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWrite,
        }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO requests (started_at, backend, method, path, format, status_code, streamed,
                                  duration_ms, request_headers, request_body, response_body,
                                  prompt_preview, response_preview)
            VALUES ($started, 'poison', 'POST', '/v1/chat/completions', 'openai-chat', 200, 0,
                    10, '{}', $body, $body, $prompt, $response)
            RETURNING id
            """;
        command.Parameters.AddWithValue("$started", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$body", new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x01 });
        command.Parameters.AddWithValue("$prompt", (object?)promptPreview ?? DBNull.Value);
        command.Parameters.AddWithValue("$response", (object?)responsePreview ?? DBNull.Value);
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task M3_GetRequest_WindowsFlattenedText_AndNeverInlinesBinary()
    {
        await using TestVessel vessel = await TestVessel.StartAsync();
        string prompt = new('p', 9_000);
        string response = new('r', 9_000);
        long id = Seed(vessel.DbPath, 1, "stub", "window-model", [], 200, null, null, prompt, response);
        long binaryId = Seed(vessel.DbPath, 1, "stub", "binary-model", [], 200, null, null, "text", "response", requestBytes: [0xff, 0x00, 0x80]);

        await using McpClient client = await Connect(vessel.BaseUrl);
        McpRequestPayload first = await GetRequest(client, id, "text", 4_000, 0);
        Assert.Equal(prompt.Length + "user: ".Length, first.Prompt!.TotalChars);
        Assert.True(first.Prompt.Truncated);
        Assert.Equal($"truncated at 4000 of {first.Prompt.TotalChars} — call again with offset=4000", first.Prompt.Note);
        Assert.True(first.Response!.Truncated);

        var assembled = new StringBuilder();
        for (int offset = 0; ; offset += 4_000)
        {
            McpRequestPayload page = await GetRequest(client, id, "text", 4_000, offset);
            assembled.Append(page.Prompt!.Text);
            if (!page.Prompt.Truncated)
            {
                break;
            }
        }

        Assert.Equal("user: " + prompt, assembled.ToString());

        McpRequestPayload binary = await GetRequest(client, binaryId, "raw", 4_000, 0);
        Assert.True(binary.Prompt!.Binary);
        Assert.Equal(3, binary.Prompt.Bytes);
        Assert.Null(binary.Prompt.Text);

        CallToolResult missing = await client.CallToolAsync(
            "get_request", new Dictionary<string, object?> { ["id"] = -1L }, cancellationToken: CT);
        Assert.True(missing.IsError is true);
    }

    [Fact]
    public async Task M4_StatsAndSessions_MatchRestAndSurfaceEstimatedTokens()
    {
        await using TestVessel vessel = await TestVessel.StartAsync();
        long sessionId = await CreateSession(vessel.BaseUrl, "MCP parity");
        Seed(vessel.DbPath, sessionId, "stub", "stats", [], 200, null, null, "one", "two", tokensIn: 10, tokensOut: 20, tokensEstimated: true);
        await using McpClient client = await Connect(vessel.BaseUrl);

        JsonElement restStats = await RestJson(vessel.BaseUrl, $"/vessel/api/stats?session={sessionId}");
        JsonElement mcpStats = await ToolJson(client, "get_stats", new Dictionary<string, object?> { ["sessionId"] = sessionId.ToString() });
        Assert.Equal(restStats.GetProperty("total").GetInt64(), mcpStats.GetProperty("total").GetInt64());
        Assert.Equal(restStats.GetProperty("tokensIn").GetInt64(), mcpStats.GetProperty("tokensIn").GetInt64());
        Assert.True(mcpStats.GetProperty("tokensEstimated").GetBoolean());

        JsonElement restSessions = await RestJson(vessel.BaseUrl, "/vessel/api/sessions");
        JsonElement mcpSessions = await ToolJson(client, "list_sessions", new Dictionary<string, object?> { ["limit"] = 20 });
        Assert.Equal(
            restSessions.EnumerateArray().Select(item => item.GetProperty("id").GetInt64()),
            mcpSessions.EnumerateArray().Select(item => item.GetProperty("id").GetInt64()));
        Assert.Equal(
            restSessions.EnumerateArray().Select(item => item.GetProperty("name").GetString()),
            mcpSessions.EnumerateArray().Select(item => item.GetProperty("name").GetString()));
        Assert.Contains("MCP parity", mcpSessions.EnumerateArray().Select(item => item.GetProperty("name").GetString()));

        // #41 — after the session-scoped clear removes rows + marker, REST and the read-only
        // MCP view converge on the same session list.
        await CreateSession(vessel.BaseUrl, "new current");
        using (var http = new HttpClient())
        using (HttpResponseMessage deleted = await http.DeleteAsync(
            $"{vessel.BaseUrl}/vessel/api/sessions/{sessionId}", CT))
        {
            Assert.Equal(System.Net.HttpStatusCode.OK, deleted.StatusCode);
        }

        JsonElement restAfterDelete = await RestJson(vessel.BaseUrl, "/vessel/api/sessions");
        JsonElement mcpAfterDelete = await ToolJson(client, "list_sessions", new Dictionary<string, object?> { ["limit"] = 20 });
        Assert.Equal(
            restAfterDelete.EnumerateArray().Select(item => item.GetProperty("id").GetInt64()),
            mcpAfterDelete.EnumerateArray().Select(item => item.GetProperty("id").GetInt64()));
        Assert.DoesNotContain("MCP parity", mcpAfterDelete.EnumerateArray().Select(item => item.GetProperty("name").GetString()));
    }

    // #87 — requests and sessions as resources: templates, a bounded recent list, reads that
    // match get_request with a single 20,000-char window, and a not-found error.
    // #94 — a tiny compressed capture must not decode past capture.maxBodyMb just because MCP
    // windows characters afterwards; the cut is reported, not presented as the whole body.
    [Fact]
    public async Task M3b_GetRequestAndResource_DecodeCompressedBodiesUnderTheCaptureBudget()
    {
        await using TestVessel vessel = await TestVessel.StartAsync(config => config.Capture.MaxBodyMb = 1);
        const int budget = 1024 * 1024;
        byte[] expanded = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            model = "bomb-model",
            choices = new[] { new { message = new { role = "assistant", content = new string('a', 4 * budget) } } },
        }));
        using var compressed = new MemoryStream();
        using (var gzip = new System.IO.Compression.GZipStream(compressed, System.IO.Compression.CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gzip.Write(expanded);
        }

        Assert.True(compressed.Length < 64 * 1024);
        long id = Seed(vessel.DbPath, 1, "stub", "bomb-model", [], 200, null, null, "hello", "unused",
            responseBytes: compressed.ToArray(), responseHeaders: """{"Content-Encoding":["gzip"]}""");

        await using McpClient client = await Connect(vessel.BaseUrl);
        McpRequestPayload raw = await GetRequest(client, id, "raw", 10, 0);
        Assert.Equal(budget, raw.Response!.TotalChars);
        Assert.Equal(10, raw.Response.Text!.Length);
        Assert.True(raw.Response.DecodeTruncated);
        Assert.False(raw.Prompt!.DecodeTruncated);

        McpRequestPayload text = await GetRequest(client, id, "text", 10, 0);
        Assert.True(text.Response!.DecodeTruncated);
        Assert.Null(text.Response.Text);
        Assert.Contains("include=raw", text.Response.Note);
        Assert.Equal("user: hell", text.Prompt!.Text);

        ReadResourceResult read = await client.ReadResourceAsync($"vessel://requests/{id}", cancellationToken: CT);
        McpRequestPayload resource = JsonSerializer.Deserialize<McpRequestPayload>(
            Assert.IsType<TextResourceContents>(Assert.Single(read.Contents)).Text,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.True(resource.Response!.DecodeTruncated);
    }

    [Fact]
    public async Task M7_Resources_TemplatesListAndRead()
    {
        await using TestVessel vessel = await TestVessel.StartAsync();
        long sessionId = await CreateSession(vessel.BaseUrl, "resource session");
        long big = Seed(vessel.DbPath, sessionId, "stub", "resource-model", [], 200, null, null, new string('p', 25_000), "short answer");
        for (int i = 0; i < 25; i++)
        {
            Seed(vessel.DbPath, sessionId, "stub", "resource-model", [], 200, null, null, $"filler {i}", "ok");
        }

        await using McpClient client = await Connect(vessel.BaseUrl);

        IList<McpClientResourceTemplate> templates = await client.ListResourceTemplatesAsync(cancellationToken: CT);
        Assert.Equal(
            ["vessel://requests/{id}", "vessel://sessions/{id}"],
            templates.Select(t => t.UriTemplate).OrderBy(uri => uri));

        IList<McpClientResource> resources = await client.ListResourcesAsync(cancellationToken: CT);
        string[] requestUris = resources.Select(r => r.Uri).Where(uri => uri.StartsWith("vessel://requests/", StringComparison.Ordinal)).ToArray();
        Assert.Equal(20, requestUris.Length);
        Assert.DoesNotContain($"vessel://requests/{big}", requestUris); // older than the 20 most recent
        Assert.Contains($"vessel://sessions/{sessionId}", resources.Select(r => r.Uri));

        ReadResourceResult requestRead = await client.ReadResourceAsync($"vessel://requests/{big}", cancellationToken: CT);
        TextResourceContents requestContents = Assert.IsType<TextResourceContents>(Assert.Single(requestRead.Contents));
        Assert.Equal("application/json", requestContents.MimeType);
        McpRequestPayload payload = JsonSerializer.Deserialize<McpRequestPayload>(
            requestContents.Text, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(20_000, payload.Prompt!.Text!.Length);
        Assert.True(payload.Prompt.Truncated);
        Assert.Equal($"truncated at 20000 of {payload.Prompt.TotalChars} — call again with offset=20000", payload.Prompt.Note);
        Assert.Equal("short answer", payload.Response!.Text);

        ReadResourceResult sessionRead = await client.ReadResourceAsync($"vessel://sessions/{sessionId}", cancellationToken: CT);
        using JsonDocument session = JsonDocument.Parse(Assert.IsType<TextResourceContents>(Assert.Single(sessionRead.Contents)).Text);
        Assert.Equal("resource session", session.RootElement.GetProperty("session").GetProperty("name").GetString());
        Assert.Equal(26, session.RootElement.GetProperty("stats").GetProperty("total").GetInt64());
        Assert.Equal(20, session.RootElement.GetProperty("recentRequests").GetArrayLength());

        await Assert.ThrowsAsync<ModelContextProtocol.McpProtocolException>(
            () => client.ReadResourceAsync("vessel://requests/999999", cancellationToken: CT).AsTask());
        await Assert.ThrowsAsync<ModelContextProtocol.McpProtocolException>(
            () => client.ReadResourceAsync("vessel://sessions/999999", cancellationToken: CT).AsTask());
    }

    // #87 review — a retained session older than list_sessions' 500-marker window is still
    // readable by URI; the cap only bounds discovery.
    [Fact]
    public async Task M7b_SessionResource_ReadsRetainedSessionOutsideListingWindow()
    {
        await using TestVessel vessel = await TestVessel.StartAsync();
        long oldSession = InsertSessions(vessel.DbPath, SessionLimits.MaxMarkers + 1);
        Seed(vessel.DbPath, oldSession, "stub", "old-session-model", [], 200, null, null, "old prompt", "old answer");

        await using McpClient client = await Connect(vessel.BaseUrl);
        IList<McpClientResource> resources = await client.ListResourcesAsync(cancellationToken: CT);
        Assert.DoesNotContain($"vessel://sessions/{oldSession}", resources.Select(r => r.Uri));

        ReadResourceResult read = await client.ReadResourceAsync($"vessel://sessions/{oldSession}", cancellationToken: CT);
        using JsonDocument session = JsonDocument.Parse(Assert.IsType<TextResourceContents>(Assert.Single(read.Contents)).Text);
        Assert.Equal(oldSession, session.RootElement.GetProperty("session").GetProperty("id").GetInt64());
        Assert.Equal(1, session.RootElement.GetProperty("session").GetProperty("requestCount").GetInt64());
        Assert.Equal(1, session.RootElement.GetProperty("recentRequests").GetArrayLength());
    }

    /// <summary>Inserts <paramref name="count"/> non-current session markers; returns the oldest id.</summary>
    private static long InsertSessions(string dbPath, int count)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWrite,
        }.ToString());
        connection.Open();
        using SqliteTransaction transaction = connection.BeginTransaction();
        long? oldest = null;
        for (int i = 0; i < count; i++)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO sessions (started_at, name, is_current) VALUES ($started, $name, 0) RETURNING id";
            command.Parameters.AddWithValue("$started", DateTime.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$name", $"bulk {i}");
            long id = Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            oldest ??= id;
        }

        transaction.Commit();
        return oldest!.Value;
    }

    [Fact]
    public async Task M5_McpEnabled_LiveConfigGateAndStatus_LeaveProxyUnaffected()
    {
        await using TestVessel vessel = await TestVessel.StartAsync();
        using var http = new HttpClient();
        VesselConfig config = await GetConfig(http, vessel.BaseUrl);
        config.Mcp.Enabled = false;

        using HttpResponseMessage put = await http.PutAsync(
            $"{vessel.BaseUrl}/vessel/api/config", AsJson(config), CT);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        using HttpResponseMessage disabled = await http.GetAsync($"{vessel.BaseUrl}/vessel/mcp", CT);
        Assert.Equal(HttpStatusCode.NotFound, disabled.StatusCode);
        Assert.Equal("not_found", disabled.Headers.GetValues("X-Vessel-Error").Single());
        using HttpResponseMessage proxied = await http.GetAsync($"{vessel.BaseUrl}/echo?mcp-disabled", CT);
        Assert.Equal(HttpStatusCode.OK, proxied.StatusCode);

        JsonElement status = await RestJson(vessel.BaseUrl, "/vessel/api/status");
        Assert.False(status.GetProperty("mcp").GetProperty("enabled").GetBoolean());

        config.Mcp.Enabled = true;
        using HttpResponseMessage enabledPut = await http.PutAsync($"{vessel.BaseUrl}/vessel/api/config", AsJson(config), CT);
        Assert.Equal(HttpStatusCode.OK, enabledPut.StatusCode);
        await using McpClient client = await Connect(vessel.BaseUrl);
        Assert.Equal(4, (await client.ListToolsAsync(cancellationToken: CT)).Count);
    }

    [Fact]
    public async Task M6_HostileHost_IsRejectedForMcp()
    {
        await using TestVessel vessel = await TestVessel.StartAsync();
        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{vessel.BaseUrl}/vessel/mcp");
        request.Headers.Host = "hostile.invalid";

        using HttpResponseMessage response = await client.SendAsync(request, CT);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("forbidden_host", response.Headers.GetValues("X-Vessel-Error").Single());
    }

    private static async Task<McpClient> Connect(string baseUrl)
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri($"{baseUrl}/vessel/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
        });
        return await McpClient.CreateAsync(transport, cancellationToken: CT);
    }

    private static async Task<McpSearchPayload> Search(McpClient client, IReadOnlyDictionary<string, object?> arguments)
    {
        JsonElement json = await ToolJson(client, "search_requests", arguments);
        return JsonSerializer.Deserialize<McpSearchPayload>(json.GetRawText(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private static async Task<long[]> SearchIds(McpClient client, IReadOnlyDictionary<string, object?> arguments) =>
        (await Search(client, arguments)).Rows.Select(row => row.Id).ToArray();

    private static async Task<McpRequestPayload> GetRequest(McpClient client, long id, string include, int maxChars, int offset)
    {
        JsonElement json = await ToolJson(client, "get_request", new Dictionary<string, object?>
        {
            ["id"] = id, ["include"] = include, ["maxChars"] = maxChars, ["offset"] = offset,
        });
        return JsonSerializer.Deserialize<McpRequestPayload>(json.GetRawText(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private static async Task<JsonElement> ToolJson(McpClient client, string name, IReadOnlyDictionary<string, object?> arguments)
    {
        CallToolResult result = await client.CallToolAsync(name, arguments, cancellationToken: CT);
        Assert.False(result.IsError is true);
        string text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        using JsonDocument document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private static async Task<long[]> RestIds(string baseUrl, string query)
    {
        JsonElement response = await RestJson(baseUrl, $"/vessel/api/requests?limit=20&{query}");
        return response.GetProperty("rows").EnumerateArray().Select(row => row.GetProperty("id").GetInt64()).ToArray();
    }

    private static async Task<JsonElement> RestJson(string baseUrl, string path)
    {
        using var http = new HttpClient();
        using HttpResponseMessage response = await http.GetAsync(baseUrl + path, CT);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CT));
        return document.RootElement.Clone();
    }

    private static string ToRestQuery(string key, string value) => key switch
    {
        "query" => "q=" + Uri.EscapeDataString(value),
        "sessionId" => "session=" + value,
        "warnedOnly" => "warned=" + (value == "true" ? "1" : "0"),
        _ => key + "=" + Uri.EscapeDataString(value),
    };

    private static object ValueForTool(string key, string value) => key switch
    {
        "sessionId" => long.Parse(value),
        "warnedOnly" => bool.Parse(value),
        _ => value,
    };

    private static async Task<long> CreateSession(string baseUrl, string name)
    {
        using var http = new HttpClient();
        using HttpResponseMessage response = await http.PostAsync(
            $"{baseUrl}/vessel/api/sessions", new StringContent($$"""{"name":"{{name}}"}""", Encoding.UTF8, "application/json"), CT);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CT));
        return document.RootElement.GetProperty("id").GetInt64();
    }

    private static async Task<VesselConfig> GetConfig(HttpClient client, string baseUrl)
    {
        using HttpResponseMessage response = await client.GetAsync($"{baseUrl}/vessel/api/config", CT);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CT));
        return JsonSerializer.Deserialize(document.RootElement.GetProperty("config").GetRawText(), ConfigJsonContext.Default.VesselConfig)!;
    }

    private static StringContent AsJson(VesselConfig config) => new(
        JsonSerializer.Serialize(config, ConfigJsonContext.Default.VesselConfig), Encoding.UTF8, "application/json");

    private static long Seed(
        string dbPath, long? sessionId, string backend, string model, string[] tags, int statusCode, string? error,
        string? warning, string prompt, string response, byte[]? requestBytes = null, long? tokensIn = null,
        long? tokensOut = null, bool tokensEstimated = false, byte[]? responseBytes = null,
        string responseHeaders = "{}")
    {
        byte[] request = requestBytes ?? Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { model, messages = new[] { new { role = "user", content = prompt } } }));
        responseBytes ??= Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            model,
            choices = new[] { new { message = new { role = "assistant", content = response } } },
        }));
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWrite,
        }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO requests (started_at, session_id, backend, tags, method, path, format, model, status_code, error,
                                  streamed, duration_ms, request_headers, response_headers, request_body, response_body,
                                  tokens_in, tokens_out, tokens_estimated, warnings)
            VALUES ($started, $session, $backend, $tags, 'POST', '/v1/chat/completions', 'openai-chat', $model, $status,
                    $error, 0, 10, '{}', $responseHeaders, $request, $response, $tokensIn, $tokensOut, $estimated, $warnings)
            RETURNING id
            """;
        command.Parameters.AddWithValue("$started", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$session", (object?)sessionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$backend", backend);
        command.Parameters.AddWithValue("$tags", JsonSerializer.Serialize(tags));
        command.Parameters.AddWithValue("$model", model);
        command.Parameters.AddWithValue("$status", statusCode);
        command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("$responseHeaders", responseHeaders);
        command.Parameters.AddWithValue("$request", BodyCompression.Compress(request));
        command.Parameters.AddWithValue("$response", BodyCompression.Compress(responseBytes));
        command.Parameters.AddWithValue("$tokensIn", (object?)tokensIn ?? DBNull.Value);
        command.Parameters.AddWithValue("$tokensOut", (object?)tokensOut ?? DBNull.Value);
        command.Parameters.AddWithValue("$estimated", tokensEstimated ? 1 : 0);
        command.Parameters.AddWithValue("$warnings", warning is null ? DBNull.Value : JsonSerializer.Serialize(new string[] { warning }));
        long id = Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);

        using SqliteCommand fts = connection.CreateCommand();
        fts.CommandText = "INSERT INTO requests_fts (rowid, prompt_text, response_text) VALUES ($id, $prompt, $response)";
        fts.Parameters.AddWithValue("$id", id);
        fts.Parameters.AddWithValue("$prompt", "user: " + prompt);
        fts.Parameters.AddWithValue("$response", response);
        fts.ExecuteNonQuery();
        return id;
    }

    /// <summary>
    /// Phase 5b follow-up: well-known OAuth discovery paths are reserved as control plane
    /// and answered with 404 X-Vessel-Error, never proxied, never captured.
    /// </summary>
    [Theory]
    [InlineData("/.well-known/oauth-authorization-server")]
    [InlineData("/.well-known/oauth-authorization-server/")]
    [InlineData("/.well-known/oauth-authorization-server/.metadata")]
    [InlineData("/.well-known/oauth-protected-resource")]
    [InlineData("/.well-known/openid-configuration")]
    public async Task WellKnownPaths_AreControlPlane_ReturnNotFound_NeverCaptured(string path)
    {
        await using TestVessel vessel = await TestVessel.StartAsync();
        using var http = new HttpClient();

        // Probe the well-known path.
        using HttpResponseMessage response = await http.GetAsync(vessel.BaseUrl + path, CT);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.True(response.Headers.Contains("X-Vessel-Error"));
        Assert.Equal("not_found", response.Headers.GetValues("X-Vessel-Error").First());

        // Verify it was never captured (no requests in the database).
        JsonElement listResponse = await RestJson(vessel.BaseUrl, "/vessel/api/requests?limit=100");
        JsonElement rows = listResponse.GetProperty("rows");
        Assert.Empty(rows.EnumerateArray());
    }

    /// <summary>
    /// Chrome DevTools auto-probes /.well-known/appspecific/com.chrome.devtools.json
    /// against the UI origin when DevTools is open. That path, and the broader
    /// /.well-known/appspecific/ prefix, is reserved as control plane: 404, never
    /// proxied, never captured.
    /// </summary>
    [Theory]
    [InlineData("/.well-known/appspecific/com.chrome.devtools.json")]
    [InlineData("/.well-known/appspecific/")]
    [InlineData("/.well-known/appspecific/other-tool.json")]
    public async Task WellKnownAppspecificPaths_AreControlPlane_ReturnNotFound_NeverCaptured(string path)
    {
        await using TestVessel vessel = await TestVessel.StartAsync();
        using var http = new HttpClient();

        using HttpResponseMessage response = await http.GetAsync(vessel.BaseUrl + path, CT);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.True(response.Headers.Contains("X-Vessel-Error"));
        Assert.Equal("not_found", response.Headers.GetValues("X-Vessel-Error").First());

        JsonElement listResponse = await RestJson(vessel.BaseUrl, "/vessel/api/requests?limit=100");
        JsonElement rows = listResponse.GetProperty("rows");
        Assert.Empty(rows.EnumerateArray());
    }

    /// <summary>
    /// Paths under /b/{backend}/.well-known/... are still proxied, not reserved.
    /// A backend that serves these paths remains reachable via the /b/ prefix.
    /// </summary>
    [Fact]
    public async Task WellKnownPaths_UnderBBackendPrefix_AreProxied()
    {
        await using TestVessel vessel = await TestVessel.StartAsync();
        using var http = new HttpClient();

        // Hit the path through the backend prefix; this should be proxied to the stub
        // and captured.
        using HttpResponseMessage response = await http.GetAsync(
            vessel.BaseUrl + "/b/stub/.well-known/openid-configuration", CT);

        // The stub's catch-all echoes the path back in a JSON payload.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Verify it WAS captured. Capture is written asynchronously by the background
        // writer, so poll for the flush rather than reading immediately (which races it).
        CapturedRow captured = await CaptureDb.WaitForRow(
            vessel.DbPath, row => row.Path == "/.well-known/openid-configuration");
        Assert.Equal("/.well-known/openid-configuration", captured.Path);
    }

    /// <summary>
    /// Same as above for the appspecific prefix: a backend that serves
    /// /.well-known/appspecific/... remains reachable through /b/{backend}/, even
    /// though the bare path is reserved as control plane.
    /// </summary>
    [Fact]
    public async Task WellKnownAppspecificPaths_UnderBBackendPrefix_AreProxied()
    {
        await using TestVessel vessel = await TestVessel.StartAsync();
        using var http = new HttpClient();

        using HttpResponseMessage response = await http.GetAsync(
            vessel.BaseUrl + "/b/stub/.well-known/appspecific/com.chrome.devtools.json", CT);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        CapturedRow captured = await CaptureDb.WaitForRow(
            vessel.DbPath, row => row.Path == "/.well-known/appspecific/com.chrome.devtools.json");
        Assert.Equal("/.well-known/appspecific/com.chrome.devtools.json", captured.Path);
    }

    /// <summary>
    /// An MCP client connect cycle should leave zero failed rows, now that well-known
    /// discovery paths are answered as control plane and never proxied.
    /// </summary>
    [Fact]
    public async Task McpClientConnectCycle_LeavesZeroFailedRows()
    {
        await using TestVessel vessel = await TestVessel.StartAsync();

        // Verify initial state: no requests.
        JsonElement initial = await RestJson(vessel.BaseUrl, "/vessel/api/requests?limit=100");
        Assert.Empty(initial.GetProperty("rows").EnumerateArray());

        // Connect an MCP client. The SDK internally probes well-known discovery paths;
        // none of those should be captured.
        await using McpClient client = await Connect(vessel.BaseUrl);
        await client.ListToolsAsync(cancellationToken: CT);

        // Verify final state: still no requests captured (well-known paths are control plane).
        JsonElement final = await RestJson(vessel.BaseUrl, "/vessel/api/requests?limit=100");
        JsonElement[] rows = final.GetProperty("rows").EnumerateArray().ToArray();
        Assert.Empty(rows);

        // Verify no failed rows in the stats.
        JsonElement statsResponse = await RestJson(vessel.BaseUrl, "/vessel/api/stats");
        long failures = statsResponse.GetProperty("failed").GetInt64();
        Assert.Equal(0, failures);
    }

    /// <summary>
    /// Favicon is served at /favicon.ico as control plane, never proxied, never captured.
    /// </summary>
    [Fact]
    public async Task Favicon_IsServedAsControlPlane()
    {
        await using TestVessel vessel = await TestVessel.StartAsync();
        using var http = new HttpClient();

        // Request the favicon.
        using HttpResponseMessage response = await http.GetAsync(vessel.BaseUrl + "/favicon.ico", CT);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/svg+xml", response.Content.Headers.ContentType?.MediaType);

        // Verify it has a long cache header.
        Assert.True(response.Headers.CacheControl?.Public);
        Assert.Equal(31536000, response.Headers.CacheControl?.MaxAge?.TotalSeconds);

        // Verify it was never captured (no requests in the database).
        JsonElement listResponse = await RestJson(vessel.BaseUrl, "/vessel/api/requests?limit=100");
        JsonElement rows = listResponse.GetProperty("rows");
        Assert.Empty(rows.EnumerateArray());

        // Verify the favicon is SVG (it should contain the svg tag and vessel mark).
        string content = await response.Content.ReadAsStringAsync(CT);
        Assert.Contains("<svg", content);
    }

    private sealed record McpSearchPayload(McpSearchRowPayload[] Rows, long? NextBefore);
    private sealed record McpSearchRowPayload(long Id, string? PromptPreview);
    private sealed record McpRequestPayload(McpBodyPayload? Prompt, McpBodyPayload? Response);
    private sealed record McpBodyPayload(
        string? Text, long TotalChars, bool Truncated, string? Note, bool Binary, long? Bytes, bool DecodeTruncated);
}
