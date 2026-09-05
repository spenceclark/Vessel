using Microsoft.Data.Sqlite;
using Vessel.Capture;
using Vessel.Config;
using Vessel.Formats;
using Vessel.Storage;
using Xunit;

namespace Vessel.Tests;

/// <summary>C12 (migrations/pragmas) and CaptureBuffer/compression unit coverage.</summary>
public class StorageTests
{
    private static readonly FormatEnricher _enricher = new(new VesselConfig());

    private static SqliteCaptureStore NewStore(string dir) =>
        new(Path.Combine(dir, "vessel.db"), new VesselConfig());

    private static EnrichedRecord MinimalRecord(string path) => _enricher.Enrich(new CaptureRecord(
        StartedAt: DateTime.UtcNow.ToString("o"),
        Seq: 0,
        SessionId: 1,
        Backend: "test",
        TagsJson: null,
        Method: "GET",
        Path: path,
        Format: "raw",
        StatusCode: 200,
        Error: null,
        Streamed: false,
        DurationMs: 1.0,
        TtftMs: null,
        VesselOverheadMs: 0.1,
        FirstResponseByteMs: null,
        LastResponseByteMs: null,
        RequestHeadersJson: "{}",
        ResponseHeadersJson: null,
        RequestBody: [1, 2, 3],
        ResponseBody: null,
        ResponseRaw: null,
        Truncated: false,
        UsageInjected: false));

    // C12: fresh DB → user_version 1, WAL, incremental auto_vacuum; reopening is a no-op.
    [Fact]
    public void Migrations_FreshDbThenReopen()
    {
        string dir = Directory.CreateTempSubdirectory("vessel-store-").FullName;
        try
        {
            using (SqliteCaptureStore store = NewStore(dir))
            {
                store.Initialize();
                store.EnsureInitialSession();
                store.InsertBatch([MinimalRecord("/first")]);
            }

            // Second open: migrations are a no-op, existing data survives.
            using (SqliteCaptureStore store = NewStore(dir))
            {
                store.Initialize();
                store.EnsureInitialSession();
                store.InsertBatch([MinimalRecord("/second")]);
            }

            using var connection = new SqliteConnection($"Data Source={Path.Combine(dir, "vessel.db")};Pooling=False");
            connection.Open();

            Assert.Equal(5L, Scalar(connection, "PRAGMA user_version"));
            Assert.Equal("wal", (string)Scalar(connection, "PRAGMA journal_mode"));
            Assert.Equal(2L, Scalar(connection, "PRAGMA auto_vacuum")); // 2 = INCREMENTAL
            Assert.Equal(2L, Scalar(connection, "SELECT COUNT(*) FROM requests"));
            Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM sessions")); // EnsureInitialSession creates "session 1" once
            Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM requests_fts"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void NamedSessionLookup_ReusesName_AndDoesNotReplaceCurrentAcrossRestart()
    {
        string dir = Directory.CreateTempSubdirectory("vessel-store-").FullName;
        try
        {
            long currentId;
            long namedId;
            using (SqliteCaptureStore store = NewStore(dir))
            {
                store.Initialize();
                currentId = store.EnsureInitialSession().Id;
                namedId = store.ResolveNamedSession("run-42").Session.Id;
                Assert.Equal(namedId, store.ResolveNamedSession("run-42").Session.Id);
                Assert.NotEqual(currentId, namedId);
            }

            using (SqliteCaptureStore store = NewStore(dir))
            {
                store.Initialize();
                Assert.Equal(currentId, store.EnsureInitialSession().Id);
                Assert.Equal(namedId, store.ResolveNamedSession("run-42").Session.Id);
            }

            using var connection = new SqliteConnection($"Data Source={Path.Combine(dir, "vessel.db")};Pooling=False");
            connection.Open();
            Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM sessions WHERE is_current = 1"));
            Assert.Equal(currentId, Scalar(connection, "SELECT id FROM sessions WHERE is_current = 1"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Retention_PrunesEmptyNonCurrentSessions_ButKeepsCurrent()
    {
        string dir = Directory.CreateTempSubdirectory("vessel-store-").FullName;
        try
        {
            long currentId;
            using (SqliteCaptureStore store = NewStore(dir))
            {
                store.Initialize();
                currentId = store.EnsureInitialSession().Id;
                store.ResolveNamedSession("empty-run");
                store.EnforceRetention();

                long clearedId = store.ResolveNamedSession("cleared-run").Session.Id;
                EnrichedRecord row = MinimalRecord("/cleared");
                row = row with
                {
                    Record = row.Record with { SessionId = clearedId },
                };
                store.InsertBatch([row]);
                Assert.Equal(1, store.Clear(beforeIso: null));
            }

            using var connection = new SqliteConnection($"Data Source={Path.Combine(dir, "vessel.db")};Pooling=False");
            connection.Open();
            Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM sessions"));
            Assert.Equal(currentId, Scalar(connection, "SELECT id FROM sessions WHERE is_current = 1"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SessionMarkerBounds_ProtectActiveIds_CapNamesAndBoundListing()
    {
        string dir = Directory.CreateTempSubdirectory("vessel-store-").FullName;
        string dbPath = Path.Combine(dir, "vessel.db");
        try
        {
            long listedCurrentId;
            using (SqliteCaptureStore store = NewStore(dir))
            {
                store.Initialize();
                long protectedId = store.EnsureInitialSession().Id;
                store.CreateSession("new current");
                var protectedIds = new HashSet<long> { protectedId };

                store.EnforceRetention(protectedIds);
                Assert.Equal(0, store.Clear(beforeIso: null, protectedIds));
                Assert.Equal(
                    new SessionDeleteResult(SessionDeleteStatus.InUse, 0),
                    store.DeleteSession(protectedId, protectedIds));

                using (var verify = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
                {
                    verify.Open();
                    Assert.Equal(1L, Scalar(verify, $"SELECT COUNT(*) FROM sessions WHERE id = {protectedId}"));
                }

                for (int i = 2; i < SessionLimits.MaxMarkers; i++)
                {
                    store.ResolveNamedSession($"run-{i}");
                }

                // The fallback to current is reported, not silent — the writer logs on
                // NameDropped so a capture landing in an unnamed session is traceable.
                long currentId = store.EnsureInitialSession().Id;
                Assert.Equal(
                    new NamedSessionResolution(store.EnsureInitialSession(), NameDropped: true),
                    store.ResolveNamedSession("one-too-many"));
                Assert.Equal(
                    new NamedSessionResolution(store.EnsureInitialSession(), NameDropped: true),
                    store.ResolveNamedSession(new string('x', SessionLimits.MaxNameLength + 1)));
                Assert.False(store.ResolveNamedSession("run-2").NameDropped);
                Assert.Equal(currentId, store.ResolveNamedSession("one-too-many").Session.Id);
                listedCurrentId = store.CreateSession("newest current beyond list cap").Id;
            }

            var readStore = new SqliteReadStore(dbPath);
            SessionInfo[] listed = readStore.ListSessions();
            Assert.Equal(SessionLimits.MaxMarkers, listed.Length);
            Assert.Contains(listed, session => session.Id == listedCurrentId && session.IsCurrent);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void DeleteSession_RemovesRowsFtsAndMarkerAtomically_LeavesOtherAndCurrent()
    {
        string dir = Directory.CreateTempSubdirectory("vessel-store-").FullName;
        string dbPath = Path.Combine(dir, "vessel.db");
        try
        {
            using (SqliteCaptureStore store = NewStore(dir))
            {
                store.Initialize();
                long currentId = store.EnsureInitialSession().Id;
                long deletedSessionId = store.ResolveNamedSession("delete-me").Session.Id;
                long keptSessionId = store.ResolveNamedSession("keep-me").Session.Id;

                EnrichedRecord deletedBase = MinimalRecord("/deleted");
                EnrichedRecord deletedRow = deletedBase with
                {
                    Record = deletedBase.Record with { SessionId = deletedSessionId },
                };
                long deletedRowId = store.InsertBatch([deletedRow])[0];
                EnrichedRecord keptBase = MinimalRecord("/kept");
                EnrichedRecord keptRow = keptBase with
                {
                    Record = keptBase.Record with { SessionId = keptSessionId, ReplayOf = deletedRowId },
                };
                long keptRowId = store.InsertBatch([keptRow])[0];

                using (var connection = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
                {
                    connection.Open();
                    using SqliteCommand fts = connection.CreateCommand();
                    fts.CommandText =
                        "INSERT INTO requests_fts(rowid, prompt_text, response_text) VALUES ($deleted, 'delete', 'delete'), ($kept, 'keep', 'keep')";
                    fts.Parameters.AddWithValue("$deleted", deletedRowId);
                    fts.Parameters.AddWithValue("$kept", keptRowId);
                    fts.ExecuteNonQuery();
                }

                Assert.Equal(
                    new SessionDeleteResult(SessionDeleteStatus.Deleted, 1),
                    store.DeleteSession(deletedSessionId));

                using var verify = new SqliteConnection($"Data Source={dbPath};Pooling=False");
                verify.Open();
                Assert.Equal(1L, Scalar(verify, "SELECT COUNT(*) FROM requests"));
                Assert.Equal(keptRowId, Scalar(verify, "SELECT id FROM requests"));
                Assert.Equal(0L, Scalar(verify, "SELECT COUNT(*) FROM requests WHERE replay_of IS NOT NULL"));
                Assert.Equal(1L, Scalar(verify, "SELECT COUNT(*) FROM requests_fts"));
                Assert.Equal(keptRowId, Scalar(verify, "SELECT rowid FROM requests_fts"));
                Assert.Equal(0L, Scalar(verify, $"SELECT COUNT(*) FROM sessions WHERE id = {deletedSessionId}"));
                Assert.Equal(1L, Scalar(verify, $"SELECT COUNT(*) FROM sessions WHERE id = {keptSessionId}"));
                Assert.Equal(1L, Scalar(verify, $"SELECT COUNT(*) FROM sessions WHERE id = {currentId} AND is_current = 1"));

                Assert.Equal(
                    new SessionDeleteResult(SessionDeleteStatus.Current, 0),
                    store.DeleteSession(currentId));
                Assert.Equal(
                    new SessionDeleteResult(SessionDeleteStatus.NotFound, 0),
                    store.DeleteSession(999_999));
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // phase-5-mcp.md §7: first-ever second migration. Build a v1 database (the original
    // schema, no preview columns) with real rows, then reopen through the current store
    // and confirm the v1→v2 upgrade runs, existing rows/FTS survive untouched, and the
    // new columns come back NULL on pre-migration rows.
    [Fact]
    public void Migrations_V1ToV2_PreservesDataAndAddsNullablePreviewColumns()
    {
        string dir = Directory.CreateTempSubdirectory("vessel-store-").FullName;
        string dbPath = Path.Combine(dir, "vessel.db");
        try
        {
            using (var connection = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
            {
                connection.Open();
                using (SqliteCommand command = connection.CreateCommand())
                {
                    command.CommandText =
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
                        """;
                    command.ExecuteNonQuery();
                }

                using (SqliteCommand command = connection.CreateCommand())
                {
                    command.CommandText = "INSERT INTO sessions (id, started_at, name) VALUES (1, '2020-01-01T00:00:00Z', 'session 1')";
                    command.ExecuteNonQuery();
                }

                using (SqliteCommand command = connection.CreateCommand())
                {
                    command.CommandText =
                        """
                        INSERT INTO requests (id, started_at, session_id, backend, method, path, format, streamed, request_headers)
                        VALUES (1, '2020-01-01T00:00:01Z', 1, 'test', 'GET', '/old', 'raw', 0, '{}')
                        """;
                    command.ExecuteNonQuery();
                }

                using (SqliteCommand command = connection.CreateCommand())
                {
                    command.CommandText = "INSERT INTO requests_fts (rowid, prompt_text, response_text) VALUES (1, 'old prompt', 'old response')";
                    command.ExecuteNonQuery();
                }

                using (SqliteCommand command = connection.CreateCommand())
                {
                    command.CommandText = "PRAGMA user_version = 1";
                    command.ExecuteNonQuery();
                }
            }

            using (SqliteCaptureStore store = NewStore(dir))
            {
                store.Initialize();
                store.InsertBatch([MinimalRecord("/new")]);
            }

            using var check = new SqliteConnection($"Data Source={dbPath};Pooling=False");
            check.Open();

            Assert.Equal(5L, Scalar(check, "PRAGMA user_version"));
            Assert.Equal(2L, Scalar(check, "SELECT COUNT(*) FROM requests"));
            Assert.Equal("/old", (string)Scalar(check, "SELECT path FROM requests WHERE id = 1"));
            Assert.Equal(1L, Scalar(check, "SELECT COUNT(*) FROM requests_fts WHERE rowid = 1"));

            using (SqliteCommand command = check.CreateCommand())
            {
                command.CommandText = "SELECT prompt_preview, response_preview FROM requests WHERE id = 1";
                using SqliteDataReader reader = command.ExecuteReader();
                Assert.True(reader.Read());
                Assert.True(reader.IsDBNull(0));
                Assert.True(reader.IsDBNull(1));
            }

            // #48 (v4) / #49 (v5) — the fan and score columns are equally nullable, and a
            // pre-migration row keeps NULLs across all three.
            using (SqliteCommand command = check.CreateCommand())
            {
                command.CommandText = "SELECT replay_group, replay_patch, score FROM requests WHERE id = 1";
                using SqliteDataReader reader = command.ExecuteReader();
                Assert.True(reader.Read());
                Assert.True(reader.IsDBNull(0));
                Assert.True(reader.IsDBNull(1));
                Assert.True(reader.IsDBNull(2));
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void BodyCompression_RoundTrips()
    {
        byte[] data = new byte[100_000];
        Random.Shared.NextBytes(data);
        Assert.Equal(data, BodyCompression.Decompress(BodyCompression.Compress(data)));

        byte[] compressible = System.Text.Encoding.UTF8.GetBytes(new string('a', 100_000));
        byte[] compressed = BodyCompression.Compress(compressible);
        Assert.True(compressed.Length < compressible.Length / 10, "highly repetitive data should compress ≥10×");
        Assert.Equal(compressible, BodyCompression.Decompress(compressed));
    }

    [Fact]
    public void CaptureBuffer_CapsAndFlags()
    {
        var buffer = new CaptureBuffer(10);
        buffer.Append([1, 2, 3, 4, 5]);
        Assert.False(buffer.Truncated);

        buffer.Append([6, 7, 8, 9, 10, 11, 12]); // crosses the cap mid-append
        Assert.True(buffer.Truncated);
        Assert.Equal(10, buffer.Length);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 9, 10], buffer.ToArrayOrNull());

        buffer.Append([99]); // past the cap: dropped
        Assert.Equal(10, buffer.Length);

        Assert.Null(new CaptureBuffer(10).ToArrayOrNull()); // empty → NULL column
    }

    [Fact]
    public void CaptureBuffer_ExactCapIsNotTruncated()
    {
        var buffer = new CaptureBuffer(4);
        buffer.Append([1, 2, 3, 4]);
        Assert.False(buffer.Truncated);
        Assert.Equal([1, 2, 3, 4], buffer.ToArrayOrNull());
    }

    // phase-5-mcp.md §7: previews are populated at write time from the enricher's already-
    // flattened text for every supported format, so search_requests never has to decode a
    // body again to render one.
    [Theory]
    [InlineData(
        "/v1/chat/completions",
        """{"model":"m","messages":[{"role":"user","content":"hello world"}]}""",
        """{"id":"x","object":"chat.completion","model":"m","choices":[{"index":0,"message":{"role":"assistant","content":"hi there"},"finish_reason":"stop"}],"usage":{"prompt_tokens":2,"completion_tokens":2,"total_tokens":4}}""")]
    [InlineData(
        "/v1/messages",
        """{"model":"m","max_tokens":10,"system":"be nice","messages":[{"role":"user","content":"hello world"}]}""",
        """{"id":"x","type":"message","role":"assistant","model":"m","content":[{"type":"text","text":"hi there"}],"stop_reason":"end_turn","usage":{"input_tokens":2,"output_tokens":2}}""")]
    [InlineData(
        "/api/chat",
        """{"model":"m","messages":[{"role":"user","content":"hello world"}]}""",
        """{"model":"m","message":{"role":"assistant","content":"hi there"},"done":true,"done_reason":"stop","eval_count":2,"eval_duration":1000000}""")]
    [InlineData(
        "/api/generate",
        """{"model":"m","prompt":"hello world"}""",
        """{"model":"m","response":"hi there","done":true,"done_reason":"stop","eval_count":2,"eval_duration":1000000}""")]
    public void InsertBatch_PopulatesPreviewColumns_ForEachFormat(string path, string requestBody, string responseBody)
    {
        string dir = Directory.CreateTempSubdirectory("vessel-store-").FullName;
        try
        {
            var enricher = new FormatEnricher(new VesselConfig(), FormatEnricher.DefaultAdapters());
            EnrichedRecord enriched = enricher.Enrich(TestCapture.Record(path, requestBody, responseBody));
            Assert.NotNull(enriched.PromptText);
            Assert.NotNull(enriched.ResponseText);

            using SqliteCaptureStore store = NewStore(dir);
            store.Initialize();
            store.EnsureInitialSession();
            IReadOnlyList<long> ids = store.InsertBatch([enriched]);

            using var connection = new SqliteConnection($"Data Source={Path.Combine(dir, "vessel.db")};Pooling=False");
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT prompt_preview, response_preview FROM requests WHERE id = $id";
            command.Parameters.AddWithValue("$id", ids[0]);
            using SqliteDataReader reader = command.ExecuteReader();
            Assert.True(reader.Read());
            Assert.False(reader.IsDBNull(0));
            Assert.False(reader.IsDBNull(1));
            Assert.Equal(CollapsedPrefix(enriched.PromptText!), reader.GetString(0));
            Assert.Equal(CollapsedPrefix(enriched.ResponseText!), reader.GetString(1));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // A row with no flattenable text (raw format) omits both preview columns, matching the
    // pre-migration NULL case search_requests already treats as optional.
    [Fact]
    public void InsertBatch_RawFormat_LeavesPreviewColumnsNull()
    {
        string dir = Directory.CreateTempSubdirectory("vessel-store-").FullName;
        try
        {
            using SqliteCaptureStore store = NewStore(dir);
            store.Initialize();
            store.EnsureInitialSession();
            IReadOnlyList<long> ids = store.InsertBatch([MinimalRecord("/raw")]);

            using var connection = new SqliteConnection($"Data Source={Path.Combine(dir, "vessel.db")};Pooling=False");
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT prompt_preview, response_preview FROM requests WHERE id = $id";
            command.Parameters.AddWithValue("$id", ids[0]);
            using SqliteDataReader reader = command.ExecuteReader();
            Assert.True(reader.Read());
            Assert.True(reader.IsDBNull(0));
            Assert.True(reader.IsDBNull(1));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string CollapsedPrefix(string text)
    {
        string collapsed = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length <= 200 ? collapsed : collapsed[..200];
    }

    private static object Scalar(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar()!;
    }
}
