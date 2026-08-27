using Microsoft.Data.Sqlite;
using Vessel.Storage;
using Xunit;

namespace Vessel.Tests;

public sealed record CapturedRow(
    long Id,
    string StartedAt,
    long? SessionId,
    string Backend,
    string? Tags,
    string Method,
    string Path,
    string Format,
    string? Model,
    long? StatusCode,
    string? Error,
    bool Streamed,
    double? DurationMs,
    double? TtftMs,
    double? VesselOverheadMs,
    double? TokPerSec,
    long? TokensIn,
    long? TokensOut,
    long? TokensCachedRead,
    long? TokensCachedWrite,
    bool TokensEstimated,
    string? StopReason,
    string? Warnings,
    string RequestHeaders,
    string? ResponseHeaders,
    byte[]? RequestBody,
    byte[]? ResponseBody,
    byte[]? ResponseRaw,
    bool Truncated)
{
    /// <summary>The row's warning codes, or an empty array when the column is NULL.</summary>
    public string[] WarningCodes => Warnings is null
        ? []
        : System.Text.Json.JsonSerializer.Deserialize<string[]>(Warnings) ?? [];
}

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
            SELECT id, started_at, session_id, backend, tags, method, path, format, model, status_code,
                   error, streamed, duration_ms, ttft_ms, vessel_overhead_ms, tok_per_sec,
                   tokens_in, tokens_out, tokens_cached_read, tokens_cached_write,
                   tokens_estimated, stop_reason, warnings,
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
                SessionId: reader.IsDBNull(2) ? null : reader.GetInt64(2),
                Backend: reader.GetString(3),
                Tags: reader.IsDBNull(4) ? null : reader.GetString(4),
                Method: reader.GetString(5),
                Path: reader.GetString(6),
                Format: reader.GetString(7),
                Model: reader.IsDBNull(8) ? null : reader.GetString(8),
                StatusCode: reader.IsDBNull(9) ? null : reader.GetInt64(9),
                Error: reader.IsDBNull(10) ? null : reader.GetString(10),
                Streamed: reader.GetInt64(11) != 0,
                DurationMs: reader.IsDBNull(12) ? null : reader.GetDouble(12),
                TtftMs: reader.IsDBNull(13) ? null : reader.GetDouble(13),
                VesselOverheadMs: reader.IsDBNull(14) ? null : reader.GetDouble(14),
                TokPerSec: reader.IsDBNull(15) ? null : reader.GetDouble(15),
                TokensIn: reader.IsDBNull(16) ? null : reader.GetInt64(16),
                TokensOut: reader.IsDBNull(17) ? null : reader.GetInt64(17),
                TokensCachedRead: reader.IsDBNull(18) ? null : reader.GetInt64(18),
                TokensCachedWrite: reader.IsDBNull(19) ? null : reader.GetInt64(19),
                TokensEstimated: reader.GetInt64(20) != 0,
                StopReason: reader.IsDBNull(21) ? null : reader.GetString(21),
                Warnings: reader.IsDBNull(22) ? null : reader.GetString(22),
                RequestHeaders: reader.GetString(23),
                ResponseHeaders: reader.IsDBNull(24) ? null : reader.GetString(24),
                RequestBody: reader.IsDBNull(25) ? null : (byte[])reader.GetValue(25),
                ResponseBody: reader.IsDBNull(26) ? null : (byte[])reader.GetValue(26),
                ResponseRaw: reader.IsDBNull(27) ? null : (byte[])reader.GetValue(27),
                Truncated: reader.GetInt64(28) != 0));
        }

        return rows;
    }

    /// <summary>Ids of rows matching an FTS query — direct <c>requests_fts MATCH</c>, no UI in the way (F8).</summary>
    public static List<long> FtsSearch(string dbPath, string match)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT rowid FROM requests_fts WHERE requests_fts MATCH $match ORDER BY rowid";
        SqliteParameter parameter = command.CreateParameter();
        parameter.ParameterName = "$match";
        parameter.Value = match;
        command.Parameters.Add(parameter);

        var ids = new List<long>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            ids.Add(reader.GetInt64(0));
        }

        return ids;
    }

    /// <summary>Count of FTS rows — for the "no orphaned FTS rows after retention" assertion (F8).</summary>
    public static long FtsCount(string dbPath)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM requests_fts";
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
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
