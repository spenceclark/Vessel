using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Vessel.Formats;

namespace Vessel.Storage;

/// <summary>
/// D2 — the UI API's read side: a separate read-only connection per call
/// (<c>Mode=ReadOnly</c>, pooled), never the writer's exclusive connection. WAL makes this
/// safe concurrently with the single writer. Ordinary UI queries are indexed (<c>id</c>
/// cursor, <c>session_id</c>) and never scan bodies; explicit #24 bulk export is the
/// deliberate exception and reads at most one body-bearing row into memory at a time.
/// </summary>
public sealed class SqliteReadStore(string dbPath)
{
    private const string SummaryColumns =
        """
        id, started_at, session_id, backend, tags, method, path, format, model, status_code,
        error, streamed, replay_of, duration_ms, ttft_ms, vessel_overhead_ms, tok_per_sec,
        tokens_in, tokens_out, tokens_cached_read, tokens_cached_write, tokens_estimated,
        stop_reason, warnings, truncated
        """;

    private const int SummaryColumnCount = 25;

    /// <summary>
    /// The most recently persisted capture for each backend, by insertion order. This is
    /// startup-only state for passive backend health; it does not generate backend traffic.
    /// </summary>
    public BackendHealthSeed[] ReadBackendHealthSeeds()
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT requests.backend, requests.started_at, requests.error
            FROM requests
            INNER JOIN (
                SELECT MAX(id) AS id
                FROM requests
                WHERE error IS NULL
                   OR error IN ('upstream_unreachable', 'upstream_timeout', 'Request', 'RequestTimedOut')
                GROUP BY backend COLLATE NOCASE
            ) latest ON latest.id = requests.id
            """;

        var outcomes = new List<BackendHealthSeed>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            outcomes.Add(new BackendHealthSeed(
                reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
        }

        return outcomes.ToArray();
    }

    /// <summary>
    /// D3/D1 — reverse-chron by id, with every filter combinable: <paramref name="before"/>
    /// and <paramref name="sessionId"/> (the cursor and session scope), plus <paramref
    /// name="q"/> (FTS5, sanitized so hostile input can never throw a syntax error),
    /// <paramref name="backend"/> (case-insensitive exact), <paramref name="model"/>/
    /// <paramref name="format"/> (exact), <paramref name="tag"/> (exact element match,
    /// never substring), <paramref name="status"/> (<c>ok</c>|<c>error</c>), and
    /// <paramref name="warned"/> (warnings present). <c>requests_fts</c> is joined only
    /// when <paramref name="q"/> actually sanitizes to something — an unconditional join
    /// would silently drop rows that never got an FTS row (no prompt/response text, e.g.
    /// raw fallback) from every unfiltered list call.
    /// </summary>
    public RequestListResponse ListRequests(
        int limit, long? before, long? sessionId,
        string? q = null, string? backend = null, string? model = null, string? format = null,
        string? tag = null, string? status = null, bool warned = false, bool includePreview = false)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        var query = new RequestQuery(sessionId, q, backend, model, format, tag, status, warned);
        string filteredFrom = ConfigureFilteredCommand(command, query, before);
        string columns = includePreview ? SummaryColumns + ", requests.prompt_preview" : SummaryColumns;
        command.CommandText = $"SELECT {columns} {filteredFrom} ORDER BY requests.id DESC LIMIT $limit";

        // Fetch one extra row so "is there a next page" doesn't require a second query.
        command.Parameters.AddWithValue("$limit", limit + 1);

        var rows = new List<Summary>(limit + 1);
        using (SqliteDataReader reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                rows.Add(ReadSummary(reader, includePreview));
            }
        }

        long? nextBefore = null;
        if (rows.Count > limit)
        {
            rows.RemoveAt(rows.Count - 1);
            nextBefore = rows[^1].Id;
        }

        return new RequestListResponse(rows.ToArray(), nextBefore);
    }

    /// <summary>#24 — count over exactly the list/export predicate.</summary>
    public long CountRequests(RequestQuery query)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        string filteredFrom = ConfigureFilteredCommand(command, query);
        command.CommandText = $"SELECT COUNT(*) {filteredFrom}";
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// #24 — newest-first export cursor. The iterator owns one read connection and reader,
    /// and materializes only the current row. Bodies are read only for the requested tier.
    /// </summary>
    public IEnumerable<ExportRow> EnumerateExport(
        RequestQuery query, ExportBodies bodies, long maxDecodedBytes)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        string filteredFrom = ConfigureFilteredCommand(command, query);
        string payloadColumns = bodies == ExportBodies.None
            ? ""
            : ", request_headers, response_headers, request_body, response_body, response_raw";
        command.CommandText =
            $"SELECT {SummaryColumns}{payloadColumns} {filteredFrom} ORDER BY requests.id DESC";

        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            Summary summary = ReadSummary(reader);
            if (bodies == ExportBodies.None)
            {
                yield return new ExportRow(summary, null, null, null, null, null, null, null);
                continue;
            }

            JsonNode? requestHeaders = JsonNode.Parse(reader.GetString(SummaryColumnCount));
            JsonNode? responseHeaders = reader.IsDBNull(SummaryColumnCount + 1)
                ? null
                : JsonNode.Parse(reader.GetString(SummaryColumnCount + 1));
            string? requestEncoding = ContentEncodingOf(requestHeaders);
            string? responseEncoding = ContentEncodingOf(responseHeaders);
            BodyPayload? requestBody = ToBodyPayload(
                reader, SummaryColumnCount + 2, requestEncoding, maxDecodedBytes);
            BodyPayload? responseBody = ToBodyPayload(
                reader, SummaryColumnCount + 3, responseEncoding, maxDecodedBytes);
            string? promptText = FlattenPrompt(summary.Format, requestBody?.Text);
            string? responseText = FlattenResponse(summary.Format, responseBody?.Text);

            if (bodies == ExportBodies.Text)
            {
                yield return new ExportRow(summary, promptText, responseText, null, null, null, null, null);
                continue;
            }

            BodyPayload? responseRaw = ToBodyPayload(
                reader, SummaryColumnCount + 4, responseEncoding, maxDecodedBytes);
            yield return new ExportRow(
                summary, promptText, responseText, requestHeaders, responseHeaders,
                requestBody, responseBody, responseRaw);
        }
    }

    public string? GetSessionName(long sessionId)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sessions WHERE id = $id";
        command.Parameters.AddWithValue("$id", sessionId);
        return command.ExecuteScalar() as string;
    }

    /// <summary>
    /// D2 — distinct <c>backend</c>/<c>model</c>/<c>format</c>/<c>tag</c> values, scoped
    /// like the list (session or all), each capped at 100 and alphabetical. No counts —
    /// the dropdowns don't need them, and it keeps the query cheap.
    /// </summary>
    public FacetsResponse GetFacets(long? sessionId)
    {
        using SqliteConnection connection = Open();

        string[] Distinct(string column)
        {
            var conditions = new List<string> { $"{column} IS NOT NULL" };
            if (sessionId is not null)
            {
                conditions.Add("session_id = $session");
            }

            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                $"SELECT DISTINCT {column} FROM requests WHERE {string.Join(" AND ", conditions)} ORDER BY {column} LIMIT 100";
            if (sessionId is long s)
            {
                command.Parameters.AddWithValue("$session", s);
            }

            var values = new List<string>();
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                values.Add(reader.GetString(0));
            }

            return values.ToArray();
        }

        string[] backends = Distinct("backend");
        string[] models = Distinct("model");
        string[] formats = Distinct("format");

        var tagConditions = new List<string> { "json_each.value IS NOT NULL" };
        if (sessionId is not null)
        {
            tagConditions.Add("requests.session_id = $session");
        }

        using SqliteCommand tagCommand = connection.CreateCommand();
        tagCommand.CommandText =
            $"""
            SELECT DISTINCT json_each.value FROM requests, json_each(COALESCE(requests.tags, '[]'))
            WHERE {string.Join(" AND ", tagConditions)}
            ORDER BY json_each.value LIMIT 100
            """;
        if (sessionId is long sid)
        {
            tagCommand.Parameters.AddWithValue("$session", sid);
        }

        var tags = new List<string>();
        using (SqliteDataReader tagReader = tagCommand.ExecuteReader())
        {
            while (tagReader.Read())
            {
                tags.Add(tagReader.GetString(0));
            }
        }

        return new FacetsResponse(backends, models, tags.ToArray(), formats);
    }

    /// <summary>
    /// D1 — splits on whitespace, wraps each token in a quoted FTS5 phrase (doubling
    /// embedded quotes to escape them), joins with implicit AND. This means FTS operators
    /// (<c>AND</c>, <c>(</c>, <c>*</c>, <c>NEAR</c>, …) are always literal text in the
    /// user's query — never a syntax error. Empty/whitespace-only input returns null (no
    /// filter, not "no results").
    /// </summary>
    private static string? SanitizeFtsQuery(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        string[] tokens = raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return null;
        }

        return string.Join(" ", tokens.Select(t => "\"" + t.Replace("\"", "\"\"") + "\""));
    }

    /// <summary>
    /// Builds and parameterizes the one canonical list predicate. Paging, count, and export
    /// all call this helper so "export what I'm looking at" cannot drift from the UI list.
    /// </summary>
    private static string ConfigureFilteredCommand(
        SqliteCommand command, RequestQuery query, long? before = null)
    {
        string? ftsQuery = SanitizeFtsQuery(query.Q);
        var where = new List<string>();
        if (before is not null) where.Add("requests.id < $before");
        if (query.SessionId is not null) where.Add("requests.session_id = $session");
        if (query.Backend is not null) where.Add("requests.backend = $backend COLLATE NOCASE");
        if (query.Model is not null) where.Add("requests.model = $model");
        if (query.Format is not null) where.Add("requests.format = $format");
        if (query.Tag is not null)
        {
            where.Add("EXISTS (SELECT 1 FROM json_each(COALESCE(requests.tags, '[]')) WHERE json_each.value = $tag)");
        }

        if (query.Status == "ok")
        {
            where.Add("(requests.error IS NULL AND (requests.status_code < 400 OR requests.status_code IS NULL))");
        }
        else if (query.Status == "error")
        {
            where.Add("(requests.error IS NOT NULL OR requests.status_code >= 400)");
        }

        if (query.Warned) where.Add("requests.warnings IS NOT NULL");
        if (ftsQuery is not null) where.Add("requests_fts MATCH $q");

        if (before is long beforeValue) command.Parameters.AddWithValue("$before", beforeValue);
        if (query.SessionId is long session) command.Parameters.AddWithValue("$session", session);
        if (query.Backend is not null) command.Parameters.AddWithValue("$backend", query.Backend);
        if (query.Model is not null) command.Parameters.AddWithValue("$model", query.Model);
        if (query.Format is not null) command.Parameters.AddWithValue("$format", query.Format);
        if (query.Tag is not null) command.Parameters.AddWithValue("$tag", query.Tag);
        if (ftsQuery is not null) command.Parameters.AddWithValue("$q", ftsQuery);

        string from = ftsQuery is null
            ? "FROM requests"
            : "FROM requests JOIN requests_fts ON requests_fts.rowid = requests.id";
        return where.Count == 0 ? from : from + " WHERE " + string.Join(" AND ", where);
    }

    /// <summary>D3 — full detail with bodies decompressed server-side, or null for an unknown id (caller writes 404).</summary>
    /// <summary>
    /// D3 — one row with headers and bodies. <paramref name="maxDecodedBytes"/> is the
    /// caller's capture budget (R05): bodies are decoded for display up to that, and flagged
    /// past it.
    /// </summary>
    public RequestDetail? GetDetail(long id, long maxDecodedBytes)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT {SummaryColumns},
                   request_headers, response_headers, request_body, response_body, response_raw
            FROM requests WHERE id = $id
            """;
        command.Parameters.AddWithValue("$id", id);

        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        Summary summary = ReadSummary(reader);
        JsonNode? requestHeaders = JsonNode.Parse(reader.GetString(SummaryColumnCount));
        JsonNode? responseHeaders = reader.IsDBNull(SummaryColumnCount + 1)
            ? null
            : JsonNode.Parse(reader.GetString(SummaryColumnCount + 1));

        string? requestEncoding = ContentEncodingOf(requestHeaders);
        string? responseEncoding = ContentEncodingOf(responseHeaders);

        BodyPayload? requestBody = ToBodyPayload(reader, SummaryColumnCount + 2, requestEncoding, maxDecodedBytes);
        BodyPayload? responseBody = ToBodyPayload(reader, SummaryColumnCount + 3, responseEncoding, maxDecodedBytes);
        BodyPayload? responseRaw = ToBodyPayload(reader, SummaryColumnCount + 4, responseEncoding, maxDecodedBytes);

        return RequestDetail.From(summary, requestHeaders, responseHeaders, requestBody, responseBody, responseRaw);
    }

    /// <summary>
    /// MCP D3 — reads a capture's stored bodies without consulting the contentless FTS
    /// index, then recreates its flattened prompt/response text at read time. The writer
    /// deliberately owns FTS population, but contentless FTS cannot return its columns;
    /// this helper therefore remains strictly on the read side and is never used by the
    /// proxy or capture writer.
    /// </summary>
    public McpRequestData? GetMcpRequest(long id)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT {SummaryColumns},
                   request_headers, response_headers, request_body, response_body, response_raw
            FROM requests WHERE id = $id
            """;
        command.Parameters.AddWithValue("$id", id);

        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        Summary summary = ReadSummary(reader);
        JsonNode? requestHeaders = JsonNode.Parse(reader.GetString(SummaryColumnCount));
        JsonNode? responseHeaders = reader.IsDBNull(SummaryColumnCount + 1)
            ? null
            : JsonNode.Parse(reader.GetString(SummaryColumnCount + 1));

        McpBodyData? requestBody = ToMcpBodyData(
            reader, SummaryColumnCount + 2, ContentEncodingOf(requestHeaders));
        McpBodyData? responseBody = ToMcpBodyData(
            reader, summary.Streamed ? SummaryColumnCount + 4 : SummaryColumnCount + 3,
            ContentEncodingOf(responseHeaders));

        // Flatten against the reassembled response body, not the wire chunk stream. This
        // is exactly the normal representation the writer's adapters receive for a
        // streamed row, and makes the same text visible without reading FTS columns.
        McpBodyData? flattenedResponseBody = ToMcpBodyData(
            reader, SummaryColumnCount + 3, ContentEncodingOf(responseHeaders));
        string? promptText = FlattenPrompt(summary.Format, requestBody?.Text);
        string? responseText = FlattenResponse(summary.Format, flattenedResponseBody?.Text);

        return new McpRequestData(summary, requestBody, responseBody, promptText, responseText);
    }

    /// <summary>Replay children of one original, newest first, for the Compare entry points.</summary>
    public Summary[] ListReplays(long replayOf)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT {SummaryColumns} FROM requests WHERE replay_of = $replay_of ORDER BY id DESC";
        command.Parameters.AddWithValue("$replay_of", replayOf);

        var rows = new List<Summary>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(ReadSummary(reader));
        }

        return rows.ToArray();
    }

    /// <summary>
    /// D3 — totals/averages, optionally scoped to one session. <paramref name="sessionId"/>
    /// null means "all". <c>failed</c> = error set or status &gt;= 400; <c>avgTtftMs</c> is
    /// averaged over streamed rows only; every average ignores null values.
    /// </summary>
    public StatsResponse GetStats(long? sessionId)
    {
        using SqliteConnection connection = Open();

        string whereClause = sessionId is null ? "" : "WHERE session_id = $session";
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT
                COUNT(*),
                SUM(CASE WHEN error IS NOT NULL OR status_code >= 400 THEN 1 ELSE 0 END),
                AVG(duration_ms),
                AVG(tok_per_sec),
                AVG(CASE WHEN streamed = 1 THEN ttft_ms ELSE NULL END),
                COALESCE(SUM(tokens_in), 0),
                COALESCE(SUM(tokens_out), 0),
                COALESCE(SUM(tokens_cached_read), 0),
                COALESCE(SUM(tokens_cached_write), 0),
                COALESCE(MAX(tokens_estimated), 0)
            FROM requests {whereClause}
            """;

        if (sessionId is long s)
        {
            command.Parameters.AddWithValue("$session", s);
        }

        using SqliteDataReader reader = command.ExecuteReader();
        reader.Read();
        long total = reader.GetInt64(0);
        long failed = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
        double? avgDuration = reader.IsDBNull(2) ? null : reader.GetDouble(2);
        double? avgTokPerSec = reader.IsDBNull(3) ? null : reader.GetDouble(3);
        double? avgTtft = reader.IsDBNull(4) ? null : reader.GetDouble(4);
        long tokensIn = reader.GetInt64(5);
        long tokensOut = reader.GetInt64(6);
        long tokensCachedRead = reader.GetInt64(7);
        long tokensCachedWrite = reader.GetInt64(8);
        bool tokensEstimated = reader.GetInt64(9) != 0;
        reader.Close();

        string? sessionStartedAt = null;
        if (sessionId is long sid)
        {
            using SqliteCommand sessionCommand = connection.CreateCommand();
            sessionCommand.CommandText = "SELECT started_at FROM sessions WHERE id = $id";
            sessionCommand.Parameters.AddWithValue("$id", sid);
            sessionStartedAt = sessionCommand.ExecuteScalar() as string;
        }

        return new StatsResponse(
            total, failed, avgDuration, avgTokPerSec, avgTtft, sessionId, sessionStartedAt,
            tokensIn, tokensOut, tokensCachedRead, tokensCachedWrite, tokensEstimated);
    }

    /// <summary>D3/#29 — newest-first, bounded, always retaining current in the result.</summary>
    public SessionInfo[] ListSessions()
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT s.id, s.started_at, s.name, s.is_current,
                   COUNT(r.id), MAX(r.started_at)
            FROM sessions s
            LEFT JOIN requests r ON r.session_id = s.id
            WHERE s.is_current = 1 OR s.id IN (
                SELECT id FROM sessions WHERE is_current = 0 ORDER BY id DESC LIMIT $other_limit
            )
            GROUP BY s.id
            ORDER BY s.id DESC
            """;
        command.Parameters.AddWithValue("$other_limit", SessionLimits.MaxMarkers - 1);

        var sessions = new List<SessionInfo>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            sessions.Add(new SessionInfo(
                reader.GetInt64(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetBoolean(3), reader.GetInt64(4), reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        return sessions.ToArray();
    }

    private static Summary ReadSummary(SqliteDataReader reader, bool includePreview = false) => new(
        Id: reader.GetInt64(0),
        StartedAt: reader.GetString(1),
        SessionId: reader.IsDBNull(2) ? null : reader.GetInt64(2),
        Backend: reader.GetString(3),
        Tags: ParseStringArray(reader, 4),
        Method: reader.GetString(5),
        Path: reader.GetString(6),
        Format: reader.GetString(7),
        Model: reader.IsDBNull(8) ? null : reader.GetString(8),
        StatusCode: reader.IsDBNull(9) ? null : reader.GetInt32(9),
        Error: reader.IsDBNull(10) ? null : reader.GetString(10),
        Streamed: reader.GetInt64(11) != 0,
        ReplayOf: reader.IsDBNull(12) ? null : reader.GetInt64(12),
        DurationMs: reader.IsDBNull(13) ? null : reader.GetDouble(13),
        TtftMs: reader.IsDBNull(14) ? null : reader.GetDouble(14),
        VesselOverheadMs: reader.IsDBNull(15) ? null : reader.GetDouble(15),
        TokPerSec: reader.IsDBNull(16) ? null : reader.GetDouble(16),
        TokensIn: reader.IsDBNull(17) ? null : reader.GetInt64(17),
        TokensOut: reader.IsDBNull(18) ? null : reader.GetInt64(18),
        TokensCachedRead: reader.IsDBNull(19) ? null : reader.GetInt64(19),
        TokensCachedWrite: reader.IsDBNull(20) ? null : reader.GetInt64(20),
        TokensEstimated: reader.GetInt64(21) != 0,
        StopReason: reader.IsDBNull(22) ? null : reader.GetString(22),
        Warnings: ParseStringArray(reader, 23),
        Truncated: reader.GetInt64(24) != 0,
        PromptPreview: includePreview && !reader.IsDBNull(SummaryColumnCount) ? reader.GetString(SummaryColumnCount) : null);

    private static string[] ParseStringArray(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? []
            : System.Text.Json.JsonSerializer.Deserialize(
                reader.GetString(ordinal), Capture.CaptureJsonContext.Default.StringArray) ?? [];

    /// <summary>
    /// Undoes storage compression (zstd), then the body's own <c>Content-Encoding</c>, then
    /// classifies: valid UTF-8 → text, otherwise → base64 (D3).
    /// <para>
    /// D01: the stored bytes are wire-true, so a gzip/br response is compressed here and
    /// would otherwise fail the UTF-8 check and render as opaque base64 — the display decode
    /// is what makes it readable, without storage having to keep a second, altered copy.
    /// R05: bounded by the same capture budget, and a body that exceeds it is flagged rather
    /// than silently shown as a complete document.
    /// </para>
    /// </summary>
    private static BodyPayload? ToBodyPayload(
        SqliteDataReader reader, int ordinal, string? contentEncoding, long maxDecodedBytes)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        byte[] stored = BodyCompression.Decompress((byte[])reader.GetValue(ordinal));

        BodyDecoder.Result decoded = BodyDecoder.Decode(stored, contentEncoding, maxDecodedBytes);
        // A body that won't decode still has to be shown: fall back to the wire bytes rather
        // than dropping the payload, so the row keeps evidence either way.
        byte[] raw = decoded.Bytes ?? stored;
        bool truncated = decoded.Status == BodyDecoder.DecodeStatus.TruncatedDecode;
        bool failed = decoded.Status == BodyDecoder.DecodeStatus.Failed;

        string text = Encoding.UTF8.GetString(raw);
        return Encoding.UTF8.GetBytes(text).AsSpan().SequenceEqual(raw)
            ? new BodyPayload(text, null, truncated, failed)
            : new BodyPayload(null, Convert.ToBase64String(raw), truncated, failed);
    }

    private static McpBodyData? ToMcpBodyData(SqliteDataReader reader, int ordinal, string? contentEncoding)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        byte[] stored = BodyCompression.Decompress((byte[])reader.GetValue(ordinal));
        // Captures are already bounded by capture.maxBodyMb. This read-side helper needs
        // the complete decoded body to report an accurate character count; it never runs
        // on the proxy or writer paths.
        BodyDecoder.Result decoded = BodyDecoder.Decode(stored, contentEncoding, long.MaxValue);
        byte[] bytes = decoded.Bytes ?? stored;

        try
        {
            string text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
            return new McpBodyData(text, null, bytes.LongLength);
        }
        catch (DecoderFallbackException)
        {
            return new McpBodyData(null, true, bytes.LongLength);
        }
    }

    private static string? FlattenPrompt(string format, string? body) => body is null
        ? null
        : format switch
        {
            FormatNames.OpenAiChat or FormatNames.OllamaChat => TextFlattener.ChatMessages(JsonUtil.Parse(body)),
            FormatNames.OpenAiResponses => TextFlattener.ResponsesInput(JsonUtil.Parse(body)),
            FormatNames.AnthropicMessages => TextFlattener.AnthropicPrompt(JsonUtil.Parse(body)),
            FormatNames.OllamaGenerate => TextFlattener.OllamaGeneratePrompt(JsonUtil.Parse(body)),
            _ => null,
        };

    private static string? FlattenResponse(string format, string? body) => body is null
        ? null
        : format switch
        {
            FormatNames.OpenAiChat => TextFlattener.OpenAiResponse(JsonUtil.Parse(body)),
            FormatNames.OpenAiResponses => TextFlattener.ResponsesOutput(JsonUtil.Parse(body)),
            FormatNames.AnthropicMessages => TextFlattener.AnthropicResponse(JsonUtil.Parse(body)),
            FormatNames.OllamaChat => TextFlattener.OllamaChatResponse(JsonUtil.Parse(body)),
            FormatNames.OllamaGenerate => TextFlattener.OllamaGenerateResponse(JsonUtil.Parse(body)),
            _ => null,
        };

    /// <summary>The <c>Content-Encoding</c> value from an already-parsed header object, or null.</summary>
    private static string? ContentEncodingOf(JsonNode? headers)
    {
        if (headers is not JsonObject obj)
        {
            return null;
        }

        foreach (KeyValuePair<string, JsonNode?> header in obj)
        {
            if (!string.Equals(header.Key, "Content-Encoding", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return header.Value is JsonArray values && values.Count > 0
                ? values[0]?.GetValue<string>()
                : null;
        }

        return null;
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = true,
        }.ToString());
        connection.Open();
        return connection;
    }
}

/// <summary>Startup seed for the last captured proxy outcome of one backend.</summary>
public sealed record BackendHealthSeed(string Backend, string StartedAt, string? Error);

/// <summary>Read-only MCP projection of a decoded capture body; binary bytes are never encoded to text.</summary>
public sealed record McpBodyData(string? Text, bool? Binary, long Bytes);

/// <summary>MCP D3's one read-side record, including text recreated from stored JSON bodies.</summary>
public sealed record McpRequestData(
    Summary Summary,
    McpBodyData? RequestBody,
    McpBodyData? ResponseBody,
    string? PromptText,
    string? ResponseText);
