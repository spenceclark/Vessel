using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Vessel.Tests;

/// <summary>D6 — <c>DELETE /requests?scope=all</c> / <c>?before=</c>: rows + FTS rows gone atomically, vacuum ran.</summary>
public class ClearTests
{
    private static CancellationToken CT => TestContext.Current.CancellationToken;

    private static async Task<int> DeletedCount(HttpResponseMessage response, CancellationToken ct)
    {
        string text = await response.Content.ReadAsStringAsync(ct);
        using JsonDocument doc = JsonDocument.Parse(text);
        return doc.RootElement.GetProperty("deleted").GetInt32();
    }

    private static long DatabaseSizeBytes(string dbPath)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT page_count * page_size FROM pragma_page_count(), pragma_page_size()";
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    // V5: clear-before leaves later rows intact, removes earlier ones, {deleted} accurate,
    // no orphaned FTS rows.
    [Fact]
    public async Task ClearBefore_RemovesOlderRows_LeavesNewerRows_NoOrphanedFts()
    {
        await using TestVessel vessel = await TestVessel.StartAsync();
        using var client = new HttpClient();

        for (int i = 1; i <= 3; i++)
        {
            await client.GetAsync($"{vessel.BaseUrl}/api/chat?marker=old&i={i}", CT);
        }

        await CaptureDb.WaitUntil(vessel.DbPath, rows => rows.Count, count => count >= 3);
        await Task.Delay(50, CT); // clear timestamp resolution from the "old" batch
        string cutoff = DateTime.UtcNow.ToString("o");
        await Task.Delay(50, CT);

        for (int i = 1; i <= 3; i++)
        {
            await client.GetAsync($"{vessel.BaseUrl}/api/chat?marker=new&i={i}", CT);
        }

        await CaptureDb.WaitUntil(vessel.DbPath, rows => rows.Count, count => count >= 6);

        long ftsCountBefore = CaptureDb.FtsCount(vessel.DbPath);
        Assert.Equal(6, ftsCountBefore); // every ollama-chat row got prompt/response text -> an FTS row

        using HttpResponseMessage response = await client.DeleteAsync(
            $"{vessel.BaseUrl}/vessel/api/requests?before={Uri.EscapeDataString(cutoff)}", CT);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, await DeletedCount(response, CT));

        List<CapturedRow> remaining = CaptureDb.Query(vessel.DbPath);
        Assert.Equal(3, remaining.Count);
        Assert.All(remaining, r => Assert.Contains("marker=new", r.Path));

        Assert.Equal(3, CaptureDb.FtsCount(vessel.DbPath)); // no orphans left behind
    }

    // V5: clear all removes everything; vacuum actually shrinks the file.
    [Fact]
    public async Task ClearAll_RemovesEverything_FileShrinks()
    {
        await using TestVessel vessel = await TestVessel.StartAsync();
        using var client = new HttpClient();

        const int bodyBytes = 200 * 1024;
        for (int i = 1; i <= 5; i++)
        {
            byte[] body = new byte[bodyBytes];
            Random.Shared.NextBytes(body); // incompressible, so the file actually grows
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{vessel.BaseUrl}/echo?i={i}")
            {
                Content = new ByteArrayContent(body),
            };
            using HttpResponseMessage r = await client.SendAsync(request, CT);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        }

        await CaptureDb.WaitUntil(vessel.DbPath, rows => rows.Count, count => count >= 5);
        long sizeBefore = DatabaseSizeBytes(vessel.DbPath);

        using HttpResponseMessage response = await client.DeleteAsync($"{vessel.BaseUrl}/vessel/api/requests?scope=all", CT);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(5, await DeletedCount(response, CT));

        Assert.Empty(CaptureDb.Query(vessel.DbPath));
        Assert.Equal(0, CaptureDb.FtsCount(vessel.DbPath));
        Assert.True(DatabaseSizeBytes(vessel.DbPath) < sizeBefore, "incremental_vacuum should have shrunk the file");
    }

    // Neither ?scope=all nor ?before= -> 400, not a silent no-op.
    [Fact]
    public async Task NeitherScopeNorBefore_400()
    {
        await using TestVessel vessel = await TestVessel.StartAsync();
        using var client = new HttpClient();

        using HttpResponseMessage response = await client.DeleteAsync($"{vessel.BaseUrl}/vessel/api/requests", CT);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", response.Headers.GetValues("X-Vessel-Error").Single());
    }
}
