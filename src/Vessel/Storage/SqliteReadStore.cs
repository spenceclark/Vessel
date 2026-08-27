using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Vessel.Storage;

/// <summary>
/// D2 — the UI API's read side: a separate read-only connection per call
/// (<c>Mode=ReadOnly</c>, pooled), never the writer's exclusive connection. WAL makes this
/// safe concurrently with the single writer. All queries are indexed (<c>id</c> cursor,
/// <c>session_id</c>); no query scans bodies.
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

    /// <summary>D3 — reverse-chron by id; <paramref name="before"/> and <paramref name="sessionId"/> are the only filters this phase.</summary>
    public RequestListResponse ListRequests(int limit, long? before, long? sessionId)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();

        var where = new List<string>();
        if (before is not null)
        {
            where.Add("id < $before");
        }

        if (sessionId is not null)
        {
            where.Add("session_id = $session");
        }

        string whereClause = where.Count == 0 ? "" : "WHERE " + string.Join(" AND ", where);
        command.CommandText = $"SELECT {SummaryColumns} FROM requests {whereClause} ORDER BY id DESC LIMIT $limit";

        if (before is long beforeVal)
        {
            command.Parameters.AddWithValue("$before", beforeVal);
        }

        if (sessionId is long sessionVal)
        {
            command.Parameters.AddWithValue("$session", sessionVal);
        }

        // Fetch one extra row so "is there a next page" doesn't require a second query.
        command.Parameters.AddWithValue("$limit", limit + 1);

        var rows = new List<Summary>(limit + 1);
        using (SqliteDataReader reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                rows.Add(ReadSummary(reader));
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

    /// <summary>D3 — full detail with bodies decompressed server-side, or null for an unknown id (caller writes 404).</summary>
    public RequestDetail? GetDetail(long id)
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

        BodyPayload? requestBody = ToBodyPayload(reader, SummaryColumnCount + 2);
        BodyPayload? responseBody = ToBodyPayload(reader, SummaryColumnCount + 3);
        BodyPayload? responseRaw = ToBodyPayload(reader, SummaryColumnCount + 4);

        return RequestDetail.From(summary, requestHeaders, responseHeaders, requestBody, responseBody, responseRaw);
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
                AVG(CASE WHEN streamed = 1 THEN ttft_ms ELSE NULL END)
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
        reader.Close();

        string? sessionStartedAt = null;
        if (sessionId is long sid)
        {
            using SqliteCommand sessionCommand = connection.CreateCommand();
            sessionCommand.CommandText = "SELECT started_at FROM sessions WHERE id = $id";
            sessionCommand.Parameters.AddWithValue("$id", sid);
            sessionStartedAt = sessionCommand.ExecuteScalar() as string;
        }

        return new StatsResponse(total, failed, avgDuration, avgTokPerSec, avgTtft, sessionId, sessionStartedAt);
    }

    /// <summary>D3 — newest-first.</summary>
    public SessionInfo[] ListSessions()
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT id, started_at, name FROM sessions ORDER BY id DESC";

        var sessions = new List<SessionInfo>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            sessions.Add(new SessionInfo(
                reader.GetInt64(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
        }

        return sessions.ToArray();
    }

    private static Summary ReadSummary(SqliteDataReader reader) => new(
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
        Truncated: reader.GetInt64(24) != 0);

    private static string[] ParseStringArray(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? []
            : System.Text.Json.JsonSerializer.Deserialize(
                reader.GetString(ordinal), Capture.CaptureJsonContext.Default.StringArray) ?? [];

    /// <summary>Decompresses the blob and classifies it: valid UTF-8 → text, otherwise → base64 (D3).</summary>
    private static BodyPayload? ToBodyPayload(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        byte[] raw = BodyCompression.Decompress((byte[])reader.GetValue(ordinal));
        string text = Encoding.UTF8.GetString(raw);
        return Encoding.UTF8.GetBytes(text).AsSpan().SequenceEqual(raw)
            ? new BodyPayload(text, null)
            : new BodyPayload(null, Convert.ToBase64String(raw));
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
