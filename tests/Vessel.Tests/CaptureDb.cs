using Microsoft.Data.Sqlite;
using Vessel.Storage;
using Xunit;

namespace Vessel.Tests;

public sealed record CapturedRow(
    long Id,
    string StartedAt,
    string Backend,
    string? Tags,
    string Method,
    string Path,
    string Format,
    long? StatusCode,
    string? Error,
    bool Streamed,
    double? DurationMs,
    double? TtftMs,
    double? VesselOverheadMs,
    string RequestHeaders,
    string? ResponseHeaders,
    byte[]? RequestBody,
    byte[]? ResponseBody,
    byte[]? ResponseRaw,
    bool Truncated);

/// <summary>
/// Read-side access to a live vessel.db for assertions: a separate read-only
/// connection (WAL allows it), polling briefly for the background writer's flush.
/// </summary>
public static class CaptureDb
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

    public static List<CapturedRow> Query(string dbPath)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, started_at, backend, tags, method, path, format, status_code,
                   error, streamed, duration_ms, ttft_ms, vessel_overhead_ms,
                   request_headers, response_headers, request_body, response_body,
                   response_raw, truncated
            FROM requests ORDER BY id
            """;

        var rows = new List<CapturedRow>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new CapturedRow(
                Id: reader.GetInt64(0),
                StartedAt: reader.GetString(1),
                Backend: reader.GetString(2),
                Tags: reader.IsDBNull(3) ? null : reader.GetString(3),
                Method: reader.GetString(4),
                Path: reader.GetString(5),
                Format: reader.GetString(6),
                StatusCode: reader.IsDBNull(7) ? null : reader.GetInt64(7),
                Error: reader.IsDBNull(8) ? null : reader.GetString(8),
                Streamed: reader.GetInt64(9) != 0,
                DurationMs: reader.IsDBNull(10) ? null : reader.GetDouble(10),
                TtftMs: reader.IsDBNull(11) ? null : reader.GetDouble(11),
                VesselOverheadMs: reader.IsDBNull(12) ? null : reader.GetDouble(12),
                RequestHeaders: reader.GetString(13),
                ResponseHeaders: reader.IsDBNull(14) ? null : reader.GetString(14),
                RequestBody: reader.IsDBNull(15) ? null : (byte[])reader.GetValue(15),
                ResponseBody: reader.IsDBNull(16) ? null : (byte[])reader.GetValue(16),
                ResponseRaw: reader.IsDBNull(17) ? null : (byte[])reader.GetValue(17),
                Truncated: reader.GetInt64(18) != 0));
        }

        return rows;
    }

    /// <summary>Polls until exactly one row matches, and returns it.</summary>
    public static async Task<CapturedRow> WaitForRow(string dbPath, Func<CapturedRow, bool> match)
    {
        List<CapturedRow> matches = await WaitUntil(dbPath, rows => rows.Where(match).ToList(), m => m.Count > 0);
        return matches.Single();
    }

    /// <summary>Polls until <paramref name="ready"/> accepts the projected rows.</summary>
    public static async Task<T> WaitUntil<T>(string dbPath, Func<List<CapturedRow>, T> project, Func<T, bool> ready)
    {
        var deadline = DateTime.UtcNow + _timeout;
        T result = project(Query(dbPath));
        while (!ready(result))
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail($"vessel.db did not reach the expected state within {_timeout.TotalSeconds:0}s");
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
            result = project(Query(dbPath));
        }

        return result;
    }

    public static byte[] Decompress(byte[]? blob)
    {
        Assert.NotNull(blob);
        return BodyCompression.Decompress(blob!);
    }
}
