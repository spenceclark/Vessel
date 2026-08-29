using Microsoft.Data.Sqlite;
using Vessel.Capture;
using Vessel.Config;
using Vessel.Formats;

namespace Vessel.Storage;

/// <summary>
/// The single-writer SQLite store: schema migrations, batched inserts, retention.
/// Only the background writer touches this; WAL lets future UI reads run concurrently.
/// D7 — two constructors: a static <see cref="VesselConfig"/> for tests (unchanged
/// behavior), and a live <see cref="ConfigStore"/> for the running app, so retention
/// re-reads its config on every batch instead of freezing it at construction.
/// </summary>
public sealed class SqliteCaptureStore : ICaptureStore, IDisposable
{
    private readonly string _dbPath;
    private readonly VesselConfig? _staticConfig;
    private readonly ConfigStore? _configStore;

    public SqliteCaptureStore(string dbPath, VesselConfig config)
    {
        _dbPath = dbPath;
        _staticConfig = config;
    }

    public SqliteCaptureStore(string dbPath, ConfigStore configStore)
    {
        _dbPath = dbPath;
        _configStore = configStore;
    }

    private RetentionConfig Retention => (_configStore?.Current ?? _staticConfig!).Retention;

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

    public string DbPath => _dbPath;

    /// <summary>Opens the database, applies pragmas and pending migrations. Fail-fast at startup.</summary>
    public void Initialize()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
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

    /// <summary>
    /// One transaction per batch (D10): each enriched row is inserted, then — for rows with
    /// flattened text — a matching contentless FTS row keyed on the same id, so search stays
    /// consistent from the moment the text exists. Bodies are zstd-compressed here, on the
    /// writer thread; the streamed <c>response_body</c> is the Vessel-synthesized document.
    /// Returns each row's new id, in <paramref name="batch"/> order, so the writer can emit
    /// the <c>completed</c> SSE event (D5) with the real DB id.
    /// </summary>
    public IReadOnlyList<long> InsertBatch(IReadOnlyList<EnrichedRecord> batch)
    {
        SqliteConnection connection = Connected();
        using SqliteTransaction transaction = connection.BeginTransaction();

        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO requests (
                started_at, session_id, backend, tags, method, path, format, model, status_code, error,
                streamed, duration_ms, ttft_ms, vessel_overhead_ms, tok_per_sec,
                tokens_in, tokens_out, tokens_cached_read, tokens_cached_write, tokens_estimated,
                stop_reason, warnings,
                request_headers, response_headers, request_body, response_body, response_raw, truncated)
            VALUES (
                $started_at, $session_id, $backend, $tags, $method, $path, $format, $model, $status_code, $error,
                $streamed, $duration_ms, $ttft_ms, $vessel_overhead_ms, $tok_per_sec,
                $tokens_in, $tokens_out, $tokens_cached_read, $tokens_cached_write, $tokens_estimated,
                $stop_reason, $warnings,
                $request_headers, $response_headers, $request_body, $response_body, $response_raw, $truncated)
            RETURNING id
            """;

        SqliteParameter Add(string name)
        {
            SqliteParameter parameter = command.CreateParameter();
            parameter.ParameterName = name;
            command.Parameters.Add(parameter);
            return parameter;
        }

        SqliteParameter startedAt = Add("$started_at");
        SqliteParameter sessionId = Add("$session_id");
        SqliteParameter backend = Add("$backend");
        SqliteParameter tags = Add("$tags");
        SqliteParameter method = Add("$method");
        SqliteParameter path = Add("$path");
        SqliteParameter format = Add("$format");
        SqliteParameter model = Add("$model");
        SqliteParameter statusCode = Add("$status_code");
        SqliteParameter error = Add("$error");
        SqliteParameter streamed = Add("$streamed");
        SqliteParameter durationMs = Add("$duration_ms");
        SqliteParameter ttftMs = Add("$ttft_ms");
        SqliteParameter overheadMs = Add("$vessel_overhead_ms");
        SqliteParameter tokPerSec = Add("$tok_per_sec");
        SqliteParameter tokensIn = Add("$tokens_in");
        SqliteParameter tokensOut = Add("$tokens_out");
        SqliteParameter tokensCachedRead = Add("$tokens_cached_read");
        SqliteParameter tokensCachedWrite = Add("$tokens_cached_write");
        SqliteParameter tokensEstimated = Add("$tokens_estimated");
        SqliteParameter stopReason = Add("$stop_reason");
        SqliteParameter warnings = Add("$warnings");
        SqliteParameter requestHeaders = Add("$request_headers");
        SqliteParameter responseHeaders = Add("$response_headers");
        SqliteParameter requestBody = Add("$request_body");
        SqliteParameter responseBody = Add("$response_body");
        SqliteParameter responseRaw = Add("$response_raw");
        SqliteParameter truncated = Add("$truncated");

        using SqliteCommand ftsCommand = connection.CreateCommand();
        ftsCommand.Transaction = transaction;
        ftsCommand.CommandText =
            "INSERT INTO requests_fts (rowid, prompt_text, response_text) VALUES ($rowid, $prompt_text, $response_text)";
        SqliteParameter ftsRowid = AddTo(ftsCommand, "$rowid");
        SqliteParameter ftsPrompt = AddTo(ftsCommand, "$prompt_text");
        SqliteParameter ftsResponse = AddTo(ftsCommand, "$response_text");

        var ids = new List<long>(batch.Count);
        foreach (EnrichedRecord enriched in batch)
        {
            CaptureRecord record = enriched.Record;
            startedAt.Value = record.StartedAt;
            sessionId.Value = record.SessionId;
            backend.Value = record.Backend;
            tags.Value = (object?)record.TagsJson ?? DBNull.Value;
            method.Value = record.Method;
            path.Value = record.Path;
            format.Value = enriched.Format;
            model.Value = (object?)enriched.Model ?? DBNull.Value;
            statusCode.Value = (object?)record.StatusCode ?? DBNull.Value;
            error.Value = (object?)record.Error ?? DBNull.Value;
            streamed.Value = record.Streamed ? 1 : 0;
            durationMs.Value = (object?)record.DurationMs ?? DBNull.Value;
            ttftMs.Value = (object?)record.TtftMs ?? DBNull.Value;
            overheadMs.Value = (object?)record.VesselOverheadMs ?? DBNull.Value;
            tokPerSec.Value = (object?)enriched.TokPerSec ?? DBNull.Value;
            tokensIn.Value = (object?)enriched.TokensIn ?? DBNull.Value;
            tokensOut.Value = (object?)enriched.TokensOut ?? DBNull.Value;
            tokensCachedRead.Value = (object?)enriched.TokensCachedRead ?? DBNull.Value;
            tokensCachedWrite.Value = (object?)enriched.TokensCachedWrite ?? DBNull.Value;
            tokensEstimated.Value = enriched.TokensEstimated ? 1 : 0;
            stopReason.Value = (object?)enriched.StopReason ?? DBNull.Value;
            warnings.Value = (object?)enriched.WarningsJson ?? DBNull.Value;
            requestHeaders.Value = record.RequestHeadersJson;
            responseHeaders.Value = (object?)record.ResponseHeadersJson ?? DBNull.Value;
            requestBody.Value = CompressOrNull(record.RequestBody);
            responseBody.Value = CompressOrNull(enriched.ReassembledResponse ?? record.ResponseBody);
            responseRaw.Value = CompressOrNull(record.ResponseRaw);
            truncated.Value = record.Truncated ? 1 : 0;

            long id = Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            ids.Add(id);

            if (enriched.PromptText is not null || enriched.ResponseText is not null)
            {
                ftsRowid.Value = id;
                ftsPrompt.Value = (object?)enriched.PromptText ?? DBNull.Value;
                ftsResponse.Value = (object?)enriched.ResponseText ?? DBNull.Value;
                ftsCommand.ExecuteNonQuery();
            }
        }

        transaction.Commit();
        return ids;
    }

    /// <summary>D4 — the newest <c>sessions</c> row, or a freshly created "session 1" on an empty database.</summary>
    public SessionInfo EnsureInitialSession()
    {
        using (SqliteCommand select = Connected().CreateCommand())
        {
            select.CommandText = "SELECT id, started_at, name FROM sessions ORDER BY id DESC LIMIT 1";
            using SqliteDataReader reader = select.ExecuteReader();
            if (reader.Read())
            {
                return new SessionInfo(
                    reader.GetInt64(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2));
            }
        }

        return InsertSession("session 1");
    }

    /// <summary>D4 — writer-thread-only insert for <c>POST /sessions</c>.</summary>
    public SessionInfo CreateSession(string? name) => InsertSession(name);

    private SessionInfo InsertSession(string? name)
    {
        using SqliteCommand insert = Connected().CreateCommand();
        string startedAt = DateTime.UtcNow.ToString("o");
        insert.CommandText = "INSERT INTO sessions (started_at, name) VALUES ($started_at, $name) RETURNING id";
        insert.Parameters.AddWithValue("$started_at", startedAt);
        insert.Parameters.AddWithValue("$name", (object?)name ?? DBNull.Value);
        long id = Convert.ToInt64(insert.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        return new SessionInfo(id, startedAt, name);
    }

    /// <summary>
    /// §6.4 — both caps, oldest-first, run by the writer after each batch. D7 — reads
    /// <see cref="Retention"/> fresh each call, so a live config PUT takes effect on the
    /// very next batch.
    /// </summary>
    public void EnforceRetention()
    {
        long excess = ExecuteScalar("SELECT COUNT(*) FROM requests") - Retention.MaxRequests;
        if (excess > 0)
        {
            DeleteOldest(excess);
            Execute("PRAGMA incremental_vacuum");
        }

        long maxBytes = (long)Retention.MaxDbSizeMb * 1024 * 1024;
        while (DatabaseSizeBytes() > maxBytes)
        {
            // Oldest-first, ~1% of rows per iteration: coarse enough to converge
            // quickly on a large overage, fine enough to never wipe recent rows
            // just to get under the cap.
            long count = ExecuteScalar("SELECT COUNT(*) FROM requests");
            long chunk = Math.Max(1, count / 100);
            long deleted = DeleteOldest(chunk);
            Execute("PRAGMA incremental_vacuum");
            if (deleted == 0)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Deletes the oldest <paramref name="limit"/> rows from <c>requests</c> and their
    /// matching contentless FTS rows in one transaction (D10), so retention never leaves an
    /// orphaned FTS row. Returns the number of <c>requests</c> rows deleted.
    /// </summary>
    private long DeleteOldest(long limit)
    {
        if (limit <= 0)
        {
            return 0;
        }

        SqliteConnection connection = Connected();
        using SqliteTransaction transaction = connection.BeginTransaction();

        const string oldest = "SELECT id FROM requests ORDER BY id LIMIT $limit";

        using (SqliteCommand fts = connection.CreateCommand())
        {
            fts.Transaction = transaction;
            fts.CommandText = $"DELETE FROM requests_fts WHERE rowid IN ({oldest})";
            AddTo(fts, "$limit").Value = limit;
            fts.ExecuteNonQuery();
        }

        long deleted;
        using (SqliteCommand rows = connection.CreateCommand())
        {
            rows.Transaction = transaction;
            rows.CommandText = $"DELETE FROM requests WHERE id IN ({oldest})";
            AddTo(rows, "$limit").Value = limit;
            deleted = rows.ExecuteNonQuery();
        }

        transaction.Commit();
        return deleted;
    }

    /// <summary>
    /// D6 — deletes <c>requests</c> rows (and their FTS rows) matching
    /// <paramref name="beforeIso"/>, or every row when null, in one transaction — same
    /// shape as <see cref="DeleteOldest"/>, filtered by <c>started_at</c> instead of an
    /// oldest-<c>N</c> limit. <c>incremental_vacuum</c> runs after commit so the file
    /// actually shrinks.
    /// </summary>
    public int Clear(string? beforeIso)
    {
        SqliteConnection connection = Connected();
        using SqliteTransaction transaction = connection.BeginTransaction();

        string filter = beforeIso is null ? "" : "WHERE started_at < $before";
        string matching = $"SELECT id FROM requests {filter}";

        using (SqliteCommand fts = connection.CreateCommand())
        {
            fts.Transaction = transaction;
            fts.CommandText = $"DELETE FROM requests_fts WHERE rowid IN ({matching})";
            if (beforeIso is not null)
            {
                AddTo(fts, "$before").Value = beforeIso;
            }

            fts.ExecuteNonQuery();
        }

        int deleted;
        using (SqliteCommand rows = connection.CreateCommand())
        {
            rows.Transaction = transaction;
            rows.CommandText = $"DELETE FROM requests {filter}";
            if (beforeIso is not null)
            {
                AddTo(rows, "$before").Value = beforeIso;
            }

            deleted = rows.ExecuteNonQuery();
        }

        transaction.Commit();
        Execute("PRAGMA incremental_vacuum");
        return deleted;
    }

    private static SqliteParameter AddTo(SqliteCommand command, string name)
    {
        SqliteParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        command.Parameters.Add(parameter);
        return parameter;
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
