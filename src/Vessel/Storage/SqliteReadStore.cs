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
        stop_reason, warnings, truncated, replay_group, replay_patch, score
        """;

    private const int SummaryColumnCount = 28;
    private const int ExportIdPageSize = 256;

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
    /// #24 — newest-first export cursor. Matching ids are fetched in small pages, then each
    /// row is fully materialized on its own short-lived read connection before it is yielded.
    /// This avoids pinning a WAL snapshot while the response waits on a slow client. Bodies
    /// are read only for the requested tier and at most one body-bearing row is held at once.
    /// </summary>
    public IEnumerable<ExportRow> EnumerateExport(
        RequestQuery query, ExportBodies bodies, long maxDecodedBytes)
    {
        long? before = null;
        while (true)
        {
            long[] ids = ReadExportIds(query, before);
            if (ids.Length == 0)
            {
                yield break;
            }

            foreach (long id in ids)
            {
                ExportRow? row = ReadExportRow(id, bodies, maxDecodedBytes);
                if (row is not null)
                {
                    yield return row;
                }
            }

            before = ids[^1];
        }
    }

    private long[] ReadExportIds(RequestQuery query, long? before)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        string filteredFrom = ConfigureFilteredCommand(command, query, before);
        command.CommandText =
            $"SELECT requests.id {filteredFrom} ORDER BY requests.id DESC LIMIT $limit";
        command.Parameters.AddWithValue("$limit", ExportIdPageSize);

        var ids = new List<long>(ExportIdPageSize);
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            ids.Add(reader.GetInt64(0));
        }

        return ids.ToArray();
    }

    private ExportRow? ReadExportRow(long id, ExportBodies bodies, long maxDecodedBytes)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        string payloadColumns = bodies == ExportBodies.None
            ? ""
            : ", request_headers, response_headers, request_body, response_body, response_raw";
        command.CommandText =
            $"SELECT {SummaryColumns}{payloadColumns} FROM requests WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);

        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            // Retention or clear can remove a row between the id page and this lookup.
            return null;
        }

        Summary summary = ReadSummary(reader);
        if (bodies == ExportBodies.None)
        {
            return new ExportRow(summary, null, null, null, null, null, null, null);
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
            return new ExportRow(summary, promptText, responseText, null, null, null, null, null);
        }

        BodyPayload? responseRaw = ToBodyPayload(
            reader, SummaryColumnCount + 4, responseEncoding, maxDecodedBytes);
        return new ExportRow(
            summary, promptText, responseText, requestHeaders, responseHeaders,
            requestBody, responseBody, responseRaw);
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
    /// Builds and parameterizes the one canonical list predicate. Paging, count, export,
    /// and the Phase 7 chart queries all call this helper so "export what I'm looking at"
    /// cannot drift from the UI list, and a report can never drift from the list either.
    /// </summary>
    /// <param name="fanOut">
    /// Phase 7 D3 (generalized #25/#26) — joins <c>json_each</c> over the tags or warnings
    /// column so a request contributes one row per tag/warning code (grouping by that
    /// dimension). The TVF argument references <c>requests</c>, so the join must follow
    /// <c>requests</c> in the FROM clause — SQLite resolves TVF arguments against tables
    /// appearing earlier only. <c>LEFT JOIN … ON TRUE</c> keeps a request with none of that
    /// column's values as a null-key row instead of dropping it.
    /// </param>
    /// <param name="extraWhere">
    /// Phase 7 D1 — additional predicates (the series metric exclusion) joined into the same
    /// AND, so a chart's count and its points reconcile against one shared clause list.
    /// </param>
    private static string ConfigureFilteredCommand(
        SqliteCommand command, RequestQuery query, long? before = null,
        FanOutColumn fanOut = FanOutColumn.None, IEnumerable<string>? extraWhere = null)
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
        if (extraWhere is not null) where.AddRange(extraWhere);

        if (before is long beforeValue) command.Parameters.AddWithValue("$before", beforeValue);
        if (query.SessionId is long session) command.Parameters.AddWithValue("$session", session);
        if (query.Backend is not null) command.Parameters.AddWithValue("$backend", query.Backend);
        if (query.Model is not null) command.Parameters.AddWithValue("$model", query.Model);
        if (query.Format is not null) command.Parameters.AddWithValue("$format", query.Format);
        if (query.Tag is not null) command.Parameters.AddWithValue("$tag", query.Tag);
        if (ftsQuery is not null) command.Parameters.AddWithValue("$q", ftsQuery);

        string from = "FROM requests";
        if (ftsQuery is not null) from += " JOIN requests_fts ON requests_fts.rowid = requests.id";
        string? fanOutSourceColumn = fanOut switch
        {
            FanOutColumn.Tags => "requests.tags",
            FanOutColumn.Warnings => "requests.warnings",
            _ => null,
        };
        if (fanOutSourceColumn is not null)
        {
            from += $" LEFT JOIN json_each(COALESCE({fanOutSourceColumn}, '[]')) AS fan_each ON TRUE";
        }

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

    /// <summary>
    /// Phase 7 D1 — the context-growth series (#25). One point per captured request (one per
    /// request × tag when grouping by tag — a request with several tags appears in each),
    /// capped to the newest <see cref="ChartLimits.MaxPoints"/> **requests** (not fanned-out
    /// rows — a multi-tag request must not let its extra rows each burn a slot of the
    /// window, or the window would silently cover fewer real requests than advertised) and
    /// returned oldest-first by id: insertion order is the true chronology; <c>started_at</c>
    /// can tie or skew. Null-metric rows are excluded by predicate, so the count and the
    /// returned points reconcile exactly — a raw capture with no token counts is not a
    /// silent gap. The count runs only when the cap was hit; the common case pays for no
    /// second scan.
    /// </summary>
    public SeriesResponse GetSeries(SeriesQuery query)
    {
        bool byTag = query.GroupBy == SeriesGroupBy.Tag;
        FanOutColumn fanOut = byTag ? FanOutColumn.Tags : FanOutColumn.None;
        string metricColumn = MetricColumn(query.Metric);
        string metricPredicate = MetricNotNullPredicate(query.Metric);
        string keyExpression = query.GroupBy switch
        {
            SeriesGroupBy.Tag => "fan_each.value",
            SeriesGroupBy.Model => "requests.model",
            SeriesGroupBy.Backend => "requests.backend",
            _ => "NULL",
        };

        using SqliteConnection connection = Open();
        // A second statement (the truncated-count query below) can follow the main one; an
        // explicit transaction pins both to the same snapshot, so a concurrent write between
        // them can never surface as a totalMatching that doesn't reconcile with the points
        // actually returned.
        using SqliteTransaction transaction = connection.BeginTransaction();
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        string filteredFrom = ConfigureFilteredCommand(
            command, query.Scope, fanOut: fanOut, extraWhere: [metricPredicate]);
        // DENSE_RANK over requests.id ranks *distinct requests* (rows fanned out from the
        // same request via the tag join share one rank), so filtering on the rank — not a
        // plain row LIMIT — caps the result to the newest MaxPoints distinct requests
        // regardless of how many tags each one carries.
        command.CommandText =
            $"""

            WITH ranked AS (
                SELECT requests.id AS id, requests.started_at AS started_at,
                       requests.tokens_estimated AS tokens_estimated,
                       {metricColumn} AS metric_value, {keyExpression} AS series_key,
                       DENSE_RANK() OVER (ORDER BY requests.id DESC) AS request_rank
                {filteredFrom}
            )
            SELECT id, started_at, tokens_estimated, metric_value, series_key
            FROM ranked
            WHERE request_rank <= $maxPointsPlusOne
            ORDER BY id DESC
            """;
        command.Parameters.AddWithValue("$maxPointsPlusOne", ChartLimits.MaxPoints + 1);

        var points = new List<(long Id, string StartedAt, bool Estimated, long Value, string? Key)>();
        using (SqliteDataReader reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                points.Add((
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetInt64(2) != 0,
                    reader.GetInt64(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4)));
            }
        }

        // request_rank <= MaxPoints+1 fetches the (MaxPoints+1)-th distinct request's rows
        // purely to learn the cap was hit; every row for that oldest overflow request is
        // dropped together here, never just one of its fanned-out rows.
        int distinctRequests = points.Select(p => p.Id).Distinct().Count();
        bool truncated = distinctRequests > ChartLimits.MaxPoints;
        if (truncated)
        {
            var newestIds = new HashSet<long>(
                points.Select(p => p.Id).Distinct().OrderByDescending(id => id).Take(ChartLimits.MaxPoints));
            points = points.Where(p => newestIds.Contains(p.Id)).ToList();
            distinctRequests = ChartLimits.MaxPoints;
        }

        points.Reverse();

        long totalMatching = 0;
        if (truncated)
        {
            totalMatching = CountMatchingRequests(connection, transaction, query.Scope, metricPredicate, fanOut);
        }

        transaction.Commit();

        // Group in insertion (oldest-first) order, then rank series by total metric value,
        // dropping the remainder past MaxSeries — never merging into an "(other)" line,
        // which would sum unrelated requests at arbitrary timestamps and draw a curve that
        // never happened. estimated describes the drawn points (the newest window), not
        // rows the cap dropped.
        // One series can carry the null key (untagged / model-less), which cannot be a
        // Dictionary key (notnull constraint), so it lives in its own slot beside the map.
        var groups = new Dictionary<string, List<(long Id, string StartedAt, long Value)>>(StringComparer.Ordinal);
        List<(long Id, string StartedAt, long Value)>? nullGroup = null;
        var order = new List<(string? Key, List<(long Id, string StartedAt, long Value)> Points)>();
        bool estimated = false;

        List<(long Id, string StartedAt, long Value)> GetOrAdd(string? key)
        {
            if (key is null)
            {
                if (nullGroup is null)
                {
                    nullGroup = [];
                    order.Add((null, nullGroup));
                }

                return nullGroup;
            }

            if (!groups.TryGetValue(key, out var group))
            {
                group = [];
                groups[key] = group;
                order.Add((key, group));
            }

            return group;
        }

        foreach (var (id, startedAt, isEstimated, value, key) in points)
        {
            GetOrAdd(key).Add((id, startedAt, value));
            estimated |= isEstimated;
        }

        var ranked = order
            .Select(g => (g.Key, g.Points, Total: g.Points.Sum(p => p.Value)))
            .OrderByDescending(g => g.Total)
            .ThenBy(g => g.Key, SeriesKeyOrder.Instance)
            .ToArray();

        return new SeriesResponse(
            Metric: MetricName(query.Metric),
            GroupBy: GroupByName(query.GroupBy),
            Series: ranked
                .Take(ChartLimits.MaxSeries)
                .Select(g => new SeriesGroup(
                    g.Key, g.Points.Select(p => new SeriesPoint(p.Id, p.StartedAt, p.Value)).ToArray()))
                .ToArray(),
            Returned: distinctRequests,
            TotalMatching: totalMatching,
            Truncated: truncated,
            OmittedSeries: Math.Max(0, ranked.Length - ChartLimits.MaxSeries),
            Estimated: estimated);
    }

    /// <summary>
    /// Phase 7 D2 — the aggregate report (#26): one grouped row per distinct key over the
    /// canonical list scope, sorted by tokens in+out desc, then requests desc, then key asc
    /// — a total order, so the chart is stable across refetches — capped at
    /// <see cref="ChartLimits.MaxGroups"/> with <see cref="AggregateResponse.TotalGroups"/>
    /// reporting the full count. No "(other)" rollup: combining averages of averages is
    /// arithmetically wrong, and a correct remainder needs a second anti-join query for a
    /// row nobody asked for. <c>by=tag</c>/<c>by=warning</c> fan out through <c>json_each</c>,
    /// so a multi-valued request is counted once per value and the rows can sum past the
    /// session total (disclosed in the UI). Groups are read in full — distinct keys over a
    /// table capped at 10 000 rows, narrow columns only — so totalGroups needs no second scan.
    /// </summary>
    public AggregateResponse GetAggregate(AggregateQuery query)
    {
        string keyExpression = query.By switch
        {
            AggregateDimension.Model => "requests.model",
            AggregateDimension.Tag => "fan_each.value",
            AggregateDimension.Backend => "requests.backend",
            AggregateDimension.Format => "requests.format",
            AggregateDimension.Patch => "requests.replay_patch",
            AggregateDimension.Warning => "fan_each.value",
            _ => throw new ArgumentOutOfRangeException(nameof(query)),
        };
        FanOutColumn fanOut = query.By switch
        {
            AggregateDimension.Tag => FanOutColumn.Tags,
            AggregateDimension.Warning => FanOutColumn.Warnings,
            _ => FanOutColumn.None,
        };

        using SqliteConnection connection = Open();
        // The main aggregate query and the percentile query below are two separate
        // statements over the same scope; an explicit transaction pins both to one snapshot
        // so a concurrent write between them can't produce totals and percentiles that don't
        // agree with each other.
        using SqliteTransaction transaction = connection.BeginTransaction();
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        // by=patch is the only dimension with a meaningless NULL key: an unpatched row did
        // not vary a parameter, so it is not a parameter set.
        string[] keyWhere = query.By == AggregateDimension.Patch ? ["requests.replay_patch IS NOT NULL"] : [];
        string filteredFrom = ConfigureFilteredCommand(command, query.Scope, fanOut: fanOut, extraWhere: keyWhere);
        command.CommandText =
            $"""

            SELECT {keyExpression},
                   COUNT(*),
                   COALESCE(SUM(CASE WHEN requests.error IS NOT NULL OR requests.status_code >= 400 THEN 1 ELSE 0 END), 0),
                   COALESCE(SUM(requests.tokens_in), 0),
                   COALESCE(SUM(requests.tokens_out), 0),
                   COALESCE(SUM(requests.tokens_cached_read), 0),
                   COALESCE(SUM(requests.tokens_cached_write), 0),
                   AVG(requests.duration_ms),
                   AVG(CASE WHEN requests.streamed = 1 THEN requests.ttft_ms ELSE NULL END),
                   AVG(requests.tok_per_sec),
                   COALESCE(MAX(requests.tokens_estimated), 0),
                   AVG(requests.score),
                   COUNT(requests.score)
            {filteredFrom}
            GROUP BY {keyExpression}
            """;

        // #26 live-use feedback — nearest-rank p50/p95 duration per group. A single extra
        // query (not a SQL window function): SQLite has no bundled PERCENTILE_CONT, and the
        // group's non-null durations are bounded by the same ≤10 000-row retention cap the
        // rest of this store already treats as a full-scan budget, so grouping and sorting
        // in C# is simpler to verify than emulating percentiles in SQL.
        var durationsByKey = new Dictionary<string, List<double>>(StringComparer.Ordinal);
        List<double>? nullKeyDurations = null;
        using (SqliteCommand percentileCommand = connection.CreateCommand())
        {
            percentileCommand.Transaction = transaction;
            string percentileFrom = ConfigureFilteredCommand(
                percentileCommand, query.Scope, fanOut: fanOut,
                extraWhere: [.. keyWhere, "requests.duration_ms IS NOT NULL"]);
            percentileCommand.CommandText = $"SELECT {keyExpression}, requests.duration_ms {percentileFrom}";
            using SqliteDataReader reader = percentileCommand.ExecuteReader();
            while (reader.Read())
            {
                double duration = reader.GetDouble(1);
                if (reader.IsDBNull(0))
                {
                    (nullKeyDurations ??= []).Add(duration);
                    continue;
                }

                string key = reader.GetString(0);
                if (!durationsByKey.TryGetValue(key, out List<double>? list))
                {
                    list = [];
                    durationsByKey[key] = list;
                }

                list.Add(duration);
            }
        }

        nullKeyDurations?.Sort();
        foreach (List<double> list in durationsByKey.Values) list.Sort();

        FanWins? fanWins = query.By is AggregateDimension.Model or AggregateDimension.Patch
            ? ReadFanWins(connection, transaction, query, keyExpression)
            : null;

        double? Percentile(string? key, double p)
        {
            List<double>? list = key is null ? nullKeyDurations : durationsByKey.GetValueOrDefault(key);
            if (list is null || list.Count == 0) return null;
            int index = Math.Clamp((int)Math.Ceiling(p * list.Count) - 1, 0, list.Count - 1);
            return list[index];
        }

        var rows = new List<AggregateRow>();
        using (SqliteDataReader reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                string? key = reader.IsDBNull(0) ? null : reader.GetString(0);
                rows.Add(new AggregateRow(
                    Key: key,
                    Requests: reader.GetInt64(1),
                    Failed: reader.GetInt64(2),
                    TokensIn: reader.GetInt64(3),
                    TokensOut: reader.GetInt64(4),
                    TokensCachedRead: reader.GetInt64(5),
                    TokensCachedWrite: reader.GetInt64(6),
                    AvgDurationMs: reader.IsDBNull(7) ? null : reader.GetDouble(7),
                    AvgTtftMs: reader.IsDBNull(8) ? null : reader.GetDouble(8),
                    AvgTokPerSec: reader.IsDBNull(9) ? null : reader.GetDouble(9),
                    TokensEstimated: reader.GetInt64(10) != 0,
                    P50DurationMs: Percentile(key, 0.50),
                    P95DurationMs: Percentile(key, 0.95),
                    MeanScore: reader.IsDBNull(11) ? null : reader.GetDouble(11),
                    Scored: reader.GetInt64(12),
                    Wins: key is null ? null : fanWins?.Wins.GetValueOrDefault(key),
                    Groups: key is null ? null : fanWins?.Groups.GetValueOrDefault(key)));
            }
        }

        transaction.Commit();

        rows.Sort((a, b) =>
        {
            int cmp = (b.TokensIn + b.TokensOut).CompareTo(a.TokensIn + a.TokensOut);
            if (cmp != 0) return cmp;
            cmp = b.Requests.CompareTo(a.Requests);
            return cmp != 0 ? cmp : SeriesKeyOrder.Instance.Compare(a.Key, b.Key);
        });

        return new AggregateResponse(
            By: DimensionName(query.By),
            Rows: rows.Take(ChartLimits.MaxGroups).ToArray(),
            TotalGroups: rows.Count);
    }

    /// <summary>
    /// Phase 7 D1 — the canonical count predicate (<see cref="CountRequests"/>' builder)
    /// plus the series metric exclusion, so a truncated chart's totalMatching counts the
    /// same rows its points came from. When fanning out the count is over the fan-out join
    /// but of request rows (<c>COUNT(DISTINCT requests.id)</c>) — the UI discloses
    /// "N requests", and a multi-tag request is one request.
    /// </summary>
    private static long CountMatchingRequests(
        SqliteConnection connection, SqliteTransaction transaction, RequestQuery query, string extraPredicate, FanOutColumn fanOut)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        string filteredFrom = ConfigureFilteredCommand(
            command, query, fanOut: fanOut, extraWhere: [extraPredicate]);
        command.CommandText =
            $"SELECT COUNT({(fanOut != FanOutColumn.None ? "DISTINCT requests.id" : "*")}) {filteredFrom}";
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string MetricColumn(SeriesMetric metric) => metric switch
    {
        SeriesMetric.TokensIn => "requests.tokens_in",
        SeriesMetric.TokensOut => "requests.tokens_out",
        // A row carrying either count contributes its total; both-null rows are the gap.
        SeriesMetric.TokensTotal => "COALESCE(requests.tokens_in, 0) + COALESCE(requests.tokens_out, 0)",
        _ => throw new ArgumentOutOfRangeException(nameof(metric)),
    };

    private static string MetricNotNullPredicate(SeriesMetric metric) => metric switch
    {
        SeriesMetric.TokensIn => "requests.tokens_in IS NOT NULL",
        SeriesMetric.TokensOut => "requests.tokens_out IS NOT NULL",
        SeriesMetric.TokensTotal => "(requests.tokens_in IS NOT NULL OR requests.tokens_out IS NOT NULL)",
        _ => throw new ArgumentOutOfRangeException(nameof(metric)),
    };

    private static string MetricName(SeriesMetric metric) => metric switch
    {
        SeriesMetric.TokensIn => "tokens_in",
        SeriesMetric.TokensOut => "tokens_out",
        SeriesMetric.TokensTotal => "tokens_total",
        _ => throw new ArgumentOutOfRangeException(nameof(metric)),
    };

    private static string GroupByName(SeriesGroupBy groupBy) => groupBy switch
    {
        SeriesGroupBy.None => "none",
        SeriesGroupBy.Tag => "tag",
        SeriesGroupBy.Model => "model",
        SeriesGroupBy.Backend => "backend",
        _ => throw new ArgumentOutOfRangeException(nameof(groupBy)),
    };

    private sealed record FanWins(
        Dictionary<string, long> Wins,
        Dictionary<string, long> Groups);

    /// <summary>
    /// #49 — replay-group win rate. A group's members are the rows carrying its
    /// <c>replay_group</c> plus the original they replay, joined through <c>replay_of</c>;
    /// the original is fetched by id rather than through the scope filter, because it is a
    /// member of the fan whether or not it falls inside the viewed session. Only scored
    /// members count; the top score wins and <em>every</em> key holding it wins — 4/4/4/5/4
    /// is the finding, not a rounding problem. A key that fields two members in one group
    /// still wins that group once, so wins never exceed groups.
    /// <para>
    /// Folded in C# for the same reason the percentiles above are: the row set is bounded by
    /// the store's retention cap, and this is far easier to verify than the equivalent SQL.
    /// </para>
    /// </summary>
    private static FanWins ReadFanWins(
        SqliteConnection connection, SqliteTransaction transaction, AggregateQuery query, string keyExpression)
    {
        var members = new Dictionary<string, List<(int? Score, string? Key)>>(StringComparer.Ordinal);
        var originalOf = new Dictionary<string, long>(StringComparer.Ordinal);
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            // Deliberately not filtered by the dimension's own key predicate: a member with no
            // patch still competes for the top score in a params fan, it just wins for nobody.
            string from = ConfigureFilteredCommand(
                command, query.Scope, extraWhere: ["requests.replay_group IS NOT NULL"]);
            command.CommandText =
                $"SELECT requests.replay_group, requests.score, {keyExpression}, requests.replay_of {from}";
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                string group = reader.GetString(0);
                if (!members.TryGetValue(group, out List<(int?, string?)>? list))
                {
                    list = [];
                    members[group] = list;
                }

                list.Add((reader.IsDBNull(1) ? null : reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
                if (!reader.IsDBNull(3))
                {
                    originalOf[group] = reader.GetInt64(3);
                }
            }
        }

        var originals = new Dictionary<long, (int? Score, string? Key)>();
        if (originalOf.Count > 0)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            string ids = string.Join(',', originalOf.Values.Distinct().Select(
                id => id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            command.CommandText = $"SELECT requests.id, requests.score, {keyExpression} FROM requests WHERE requests.id IN ({ids})";
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                originals[reader.GetInt64(0)] = (
                    reader.IsDBNull(1) ? null : reader.GetInt32(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2));
            }
        }

        var wins = new Dictionary<string, long>(StringComparer.Ordinal);
        var groups = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach ((string group, List<(int? Score, string? Key)> list) in members)
        {
            List<(int? Score, string? Key)> all = [.. list];
            if (originalOf.TryGetValue(group, out long originalId)
                && originals.TryGetValue(originalId, out (int? Score, string? Key) original))
            {
                all.Add(original);
            }

            (int? Score, string? Key)[] scored = [.. all.Where(member => member.Score is not null)];
            if (scored.Length == 0)
            {
                continue;
            }

            int top = scored.Max(member => member.Score!.Value);
            foreach (string key in scored.Where(m => m.Key is not null).Select(m => m.Key!).Distinct(StringComparer.Ordinal))
            {
                groups[key] = groups.GetValueOrDefault(key) + 1;
            }

            foreach (string key in scored
                .Where(m => m.Key is not null && m.Score!.Value == top)
                .Select(m => m.Key!)
                .Distinct(StringComparer.Ordinal))
            {
                wins[key] = wins.GetValueOrDefault(key) + 1;
            }
        }

        return new FanWins(wins, groups);
    }

    private static string DimensionName(AggregateDimension by) => by switch
    {
        AggregateDimension.Model => "model",
        AggregateDimension.Tag => "tag",
        AggregateDimension.Backend => "backend",
        AggregateDimension.Format => "format",
        AggregateDimension.Patch => "patch",
        AggregateDimension.Warning => "warning",
        _ => throw new ArgumentOutOfRangeException(nameof(by)),
    };

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
        ReplayGroup: reader.IsDBNull(25) ? null : reader.GetString(25),
        ReplayPatch: reader.IsDBNull(26) ? null : reader.GetString(26),
        Score: reader.IsDBNull(27) ? null : reader.GetInt32(27),
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
