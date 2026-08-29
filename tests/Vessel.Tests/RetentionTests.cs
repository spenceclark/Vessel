using System.Net;
using Xunit;

namespace Vessel.Tests;

/// <summary>C9/C10: both retention caps, enforced by the writer after each batch (§6.4).</summary>
public class RetentionTests
{
    private static CancellationToken CT => TestContext.Current.CancellationToken;

    // C9: maxRequests — oldest rows deleted, newest kept.
    [Fact]
    public async Task MaxRequests_OldestDeleted_NewestKept()
    {
        await using TestVessel vessel = await TestVessel.StartAsync(c => c.Retention.MaxRequests = 5);
        using var client = new HttpClient();

        const int total = 12;
        for (int i = 1; i <= total; i++)
        {
            using HttpResponseMessage response = await client.GetAsync($"{vessel.BaseUrl}/echo?i={i:00}", CT);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // Wait until the last request has landed and the cap is enforced.
        List<CapturedRow> rows = await CaptureDb.WaitUntil(
            vessel.DbPath,
            rows => rows,
            rows => rows.Any(r => r.Path.Contains($"i={total}")) && rows.Count <= 5);

        Assert.Equal(5, rows.Count);
        Assert.Equal(
            Enumerable.Range(total - 4, 5).Select(i => $"/echo?i={i:00}"),
            rows.Select(r => r.Path));
    }

    // C10: maxDbSizeMb — incompressible bodies push the file over a 1 MB cap; the
    // writer deletes oldest and vacuums until back under.
    [Fact]
    public async Task MaxDbSize_FileShrinksUnderCap()
    {
        await using TestVessel vessel = await TestVessel.StartAsync(c => c.Retention.MaxDbSizeMb = 1);
        using var client = new HttpClient();

        const int bodyBytes = 200 * 1024;
        const int total = 10;
        for (int i = 1; i <= total; i++)
        {
            byte[] body = new byte[bodyBytes];
            Random.Shared.NextBytes(body); // incompressible — zstd can't shrink it
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{vessel.BaseUrl}/echo?i={i:00}")
            {
                Content = new ByteArrayContent(body),
            };
            using HttpResponseMessage response = await client.SendAsync(request, CT);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // R28 — rows and file size must come from one stable database state. Reading them
        // via two separately-opened connections let a delete-and-vacuum land between the two
        // reads: the row list still showed all 10 pre-retention rows while the size read,
        // taken a moment later, already reflected the post-retention file, so the predicate
        // was satisfied by a stale row list. `QueryWithSize` reads both inside one WAL read
        // transaction, so they always describe the same moment.
        (List<CapturedRow> rows, long _) = await CaptureDb.WaitUntilWithSize(
            vessel.DbPath,
            snapshot => snapshot.Rows.Any(r => r.Path.Contains($"i={total}"))
                && snapshot.SizeBytes <= 1024 * 1024);

        // Oldest rows were sacrificed, newest survived.
        Assert.True(rows.Count < total, $"expected deletions, all {total} rows still present");
        Assert.Contains(rows, r => r.Path.Contains($"i={total}"));
        Assert.DoesNotContain(rows, r => r.Path.Contains("i=01"));
    }
}
