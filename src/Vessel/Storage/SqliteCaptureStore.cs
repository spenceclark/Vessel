using Microsoft.Data.Sqlite;
using Vessel.Capture;
using Vessel.Config;

namespace Vessel.Storage;

/// <summary>
/// The single-writer SQLite store: schema migrations, batched inserts, retention.
/// Only the background writer touches this; WAL lets future UI reads run concurrently.
/// </summary>
public sealed class SqliteCaptureStore(string dbPath, VesselConfig config) : IDisposable
{

    private static readonly string[] _migrations =
    [
        // v1 — architecture.md §6.2 verbatim. sessions and requests_fts are created now
        // but stay unpopulated until Phases 3 and 2/4 respectively.
        """
        CREATE TABLE requests (
            id                  INTEGER PRIMARY KEY,
            started_at          TEXT NOT NULL,
            session_id          INTEGER REFERENCES sessions(id),
            backend             TEXT NOT NULL,
            tags                TEXT,
            method              TEXT NOT NULL,
            path                TEXT NOT NULL,
            format              TEXT NOT NULL,
            model               TEXT,
            status_code         INTEGER,
            error               TEXT,
            streamed            INTEGER NOT NULL DEFAULT 0,
            replay_of           INTEGER REFERENCES requests(id),
            duration_ms         REAL,
            ttft_ms             REAL,
            vessel_overhead_ms  REAL,
            tok_per_sec         REAL,
            tokens_in           INTEGER,
            tokens_out          INTEGER,
            tokens_cached_read  INTEGER,
            tokens_cached_write INTEGER,
            tokens_estimated    INTEGER NOT NULL DEFAULT 0,
            stop_reason         TEXT,
            warnings            TEXT,
            cost_estimate       REAL,
            request_headers     TEXT NOT NULL,
            response_headers    TEXT,
            request_body        BLOB,
            response_body       BLOB,
            response_raw        BLOB,
            truncated           INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX ix_requests_started ON requests(started_at);
        CREATE INDEX ix_requests_session ON requests(session_id);

        CREATE TABLE sessions (
            id          INTEGER PRIMARY KEY,
            started_at  TEXT NOT NULL,
            name        TEXT
        );

        CREATE VIRTUAL TABLE requests_fts USING fts5(
            prompt_text, response_text, content='', contentless_delete=1
        );
        """,
    ];

    private SqliteConnection? _connection;

    public string DbPath => dbPath;

    /// <summary>Opens the database, applies pragmas and pending migrations. Fail-fast at startup.</summary>
    public void Initialize()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        connection.Open();
        _connection = connection;

        // auto_vacuum must be decided before any table exists; it's a no-op on a
        // database that already has one.
        Execute("PRAGMA auto_vacuum = INCREMENTAL");
        Execute("PRAGMA journal_mode = WAL");
        Execute("PRAGMA synchronous = NORMAL");

        long version = ExecuteScalar("PRAGMA user_version");
        for (long v = version; v < _migrations.Length; v++)
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            using (SqliteCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = _migrations[v];
                command.ExecuteNonQuery();
            }

            using (SqliteCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = $"PRAGMA user_version = {v + 1}";
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    /// <summary>One transaction per batch; bodies are zstd-compressed here, on the writer thread.</summary>
    public void InsertBatch(IReadOnlyList<CaptureRecord> batch)
    {
        SqliteConnection connection = Connected();
        using SqliteTransaction transaction = connection.BeginTransaction();
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO requests (
                started_at, backend, tags, method, path, format, status_code, error,
                streamed, duration_ms, ttft_ms, vessel_overhead_ms,
                request_headers, response_headers, request_body, response_body,
                response_raw, truncated)
            VALUES (
                $started_at, $backend, $tags, $method, $path, $format, $status_code, $error,
                $streamed, $duration_ms, $ttft_ms, $vessel_overhead_ms,
                $request_headers, $response_headers, $request_body, $response_body,
                $response_raw, $truncated)
            """;

        SqliteParameter Add(string name)
        {
            SqliteParameter parameter = command.CreateParameter();
            parameter.ParameterName = name;
            command.Parameters.Add(parameter);
            return parameter;
        }

        SqliteParameter startedAt = Add("$started_at");
        SqliteParameter backend = Add("$backend");
        SqliteParameter tags = Add("$tags");
        SqliteParameter method = Add("$method");
        SqliteParameter path = Add("$path");
        SqliteParameter format = Add("$format");
        SqliteParameter statusCode = Add("$status_code");
        SqliteParameter error = Add("$error");
        SqliteParameter streamed = Add("$streamed");
        SqliteParameter durationMs = Add("$duration_ms");
        SqliteParameter ttftMs = Add("$ttft_ms");
        SqliteParameter overheadMs = Add("$vessel_overhead_ms");
        SqliteParameter requestHeaders = Add("$request_headers");
        SqliteParameter responseHeaders = Add("$response_headers");
        SqliteParameter requestBody = Add("$request_body");
        SqliteParameter responseBody = Add("$response_body");
        SqliteParameter responseRaw = Add("$response_raw");
        SqliteParameter truncated = Add("$truncated");

        foreach (CaptureRecord record in batch)
        {
            startedAt.Value = record.StartedAt;
            backend.Value = record.Backend;
            tags.Value = (object?)record.TagsJson ?? DBNull.Value;
            method.Value = record.Method;
            path.Value = record.Path;
            format.Value = record.Format;
            statusCode.Value = (object?)record.StatusCode ?? DBNull.Value;
            error.Value = (object?)record.Error ?? DBNull.Value;
            streamed.Value = record.Streamed ? 1 : 0;
            durationMs.Value = (object?)record.DurationMs ?? DBNull.Value;
            ttftMs.Value = (object?)record.TtftMs ?? DBNull.Value;
            overheadMs.Value = (object?)record.VesselOverheadMs ?? DBNull.Value;
            requestHeaders.Value = record.RequestHeadersJson;
            responseHeaders.Value = (object?)record.ResponseHeadersJson ?? DBNull.Value;
            requestBody.Value = CompressOrNull(record.RequestBody);
            responseBody.Value = CompressOrNull(record.ResponseBody);
            responseRaw.Value = CompressOrNull(record.ResponseRaw);
            truncated.Value = record.Truncated ? 1 : 0;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>§6.4 — both caps, oldest-first, run by the writer after each batch.</summary>
    public void EnforceRetention()
    {
        long excess = ExecuteScalar("SELECT COUNT(*) FROM requests") - config.Retention.MaxRequests;
        if (excess > 0)
        {
            Execute($"DELETE FROM requests WHERE id IN (SELECT id FROM requests ORDER BY id LIMIT {excess})");
            Execute("PRAGMA incremental_vacuum");
        }

        long maxBytes = (long)config.Retention.MaxDbSizeMb * 1024 * 1024;
        while (DatabaseSizeBytes() > maxBytes)
        {
            // Oldest-first, ~1% of rows per iteration: coarse enough to converge
            // quickly on a large overage, fine enough to never wipe recent rows
            // just to get under the cap.
            long count = ExecuteScalar("SELECT COUNT(*) FROM requests");
            long chunk = Math.Max(1, count / 100);
            long deleted = ExecuteScalar(
                $"DELETE FROM requests WHERE id IN (SELECT id FROM requests ORDER BY id LIMIT {chunk}); SELECT changes()");
            Execute("PRAGMA incremental_vacuum");
            if (deleted == 0)
            {
                break;
            }
        }
    }

    private long DatabaseSizeBytes() =>
        ExecuteScalar("SELECT page_count * page_size FROM pragma_page_count(), pragma_page_size()");

    private static object CompressOrNull(byte[]? body) =>
        body is null ? DBNull.Value : BodyCompression.Compress(body);

    private void Execute(string sql)
    {
        using SqliteCommand command = Connected().CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private long ExecuteScalar(string sql)
    {
        using SqliteCommand command = Connected().CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private SqliteConnection Connected() =>
        _connection ?? throw new InvalidOperationException("SqliteCaptureStore.Initialize was not called");

    public void Dispose() => _connection?.Dispose();
}
