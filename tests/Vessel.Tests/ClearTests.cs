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

    [Fact]
    public async Task DeleteSession_RunsAsScopedClear_ProtectsCurrentAndReportsMissing()
    {
        await using TestVessel vessel = await TestVessel.StartAsync();
        using var client = new HttpClient();

        using HttpResponseMessage currentRequest = await client.GetAsync($"{vessel.BaseUrl}/api/chat?current", CT);
        Assert.Equal(HttpStatusCode.OK, currentRequest.StatusCode);

        using var namedRequest = new HttpRequestMessage(HttpMethod.Get, $"{vessel.BaseUrl}/api/chat?named");
        namedRequest.Headers.Add("X-Vessel-Session", "delete-me");
        using HttpResponseMessage namedResponse = await client.SendAsync(namedRequest, CT);
        Assert.Equal(HttpStatusCode.OK, namedResponse.StatusCode);
        await CaptureDb.WaitUntil(vessel.DbPath, rows => rows.Count, count => count >= 2);

        using HttpResponseMessage listResponse = await client.GetAsync($"{vessel.BaseUrl}/vessel/api/sessions", CT);
        using JsonDocument sessions = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync(CT));
        JsonElement named = sessions.RootElement.EnumerateArray()
            .Single(session => session.GetProperty("name").GetString() == "delete-me");
        JsonElement current = sessions.RootElement.EnumerateArray()
            .Single(session => session.GetProperty("isCurrent").GetBoolean());
        long namedId = named.GetProperty("id").GetInt64();
        long currentId = current.GetProperty("id").GetInt64();

        using HttpResponseMessage deleted = await client.DeleteAsync(
            $"{vessel.BaseUrl}/vessel/api/sessions/{namedId}", CT);
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.Equal(1, await DeletedCount(deleted, CT));
        Assert.Single(CaptureDb.Query(vessel.DbPath));

        using HttpResponseMessage afterResponse = await client.GetAsync($"{vessel.BaseUrl}/vessel/api/sessions", CT);
        using JsonDocument after = JsonDocument.Parse(await afterResponse.Content.ReadAsStringAsync(CT));
        Assert.DoesNotContain(after.RootElement.EnumerateArray(), session => session.GetProperty("id").GetInt64() == namedId);

        using HttpResponseMessage currentDelete = await client.DeleteAsync(
            $"{vessel.BaseUrl}/vessel/api/sessions/{currentId}", CT);
        Assert.Equal(HttpStatusCode.Conflict, currentDelete.StatusCode);
        Assert.Equal("invalid_request", currentDelete.Headers.GetValues("X-Vessel-Error").Single());

        using HttpResponseMessage missing = await client.DeleteAsync(
            $"{vessel.BaseUrl}/vessel/api/sessions/999999", CT);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("not_found", missing.Headers.GetValues("X-Vessel-Error").Single());
    }
}
