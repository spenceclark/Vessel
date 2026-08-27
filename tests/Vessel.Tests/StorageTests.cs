using Microsoft.Data.Sqlite;
using Vessel.Capture;
using Vessel.Config;
using Vessel.Storage;
using Xunit;

namespace Vessel.Tests;

/// <summary>C12 (migrations/pragmas) and CaptureBuffer/compression unit coverage.</summary>
public class StorageTests
{
    private static SqliteCaptureStore NewStore(string dir) =>
        new(Path.Combine(dir, "vessel.db"), new VesselConfig());

    private static CaptureRecord MinimalRecord(string path) => new(
        StartedAt: DateTime.UtcNow.ToString("o"),
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
        RequestHeadersJson: "{}",
        ResponseHeadersJson: null,
        RequestBody: [1, 2, 3],
        ResponseBody: null,
        ResponseRaw: null,
        Truncated: false);

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
                store.InsertBatch([MinimalRecord("/first")]);
            }

            // Second open: migrations are a no-op, existing data survives.
            using (SqliteCaptureStore store = NewStore(dir))
            {
                store.Initialize();
                store.InsertBatch([MinimalRecord("/second")]);
            }

            using var connection = new SqliteConnection($"Data Source={Path.Combine(dir, "vessel.db")};Pooling=False");
            connection.Open();

            Assert.Equal(1L, Scalar(connection, "PRAGMA user_version"));
            Assert.Equal("wal", (string)Scalar(connection, "PRAGMA journal_mode"));
            Assert.Equal(2L, Scalar(connection, "PRAGMA auto_vacuum")); // 2 = INCREMENTAL
            Assert.Equal(2L, Scalar(connection, "SELECT COUNT(*) FROM requests"));
            Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM sessions"));
            Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM requests_fts"));
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

    private static object Scalar(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar()!;
    }
}
