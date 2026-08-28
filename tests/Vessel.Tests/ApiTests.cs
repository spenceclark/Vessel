using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Vessel.Tests;

/// <summary>
/// Phase 3 §3 U1–U4, U7 — the read-side REST API (list/detail/stats/sessions) and the
/// embedded-UI/placeholder routing. Each test gets its own <see cref="TestVessel"/> (fresh
/// DB) so row counts and cursors are exact, not marker-filtered out of a shared fixture.
/// </summary>
public class ApiTests
{
    private static CancellationToken CT => TestContext.Current.CancellationToken;

    private static async Task<JsonElement> GetJson(HttpClient client, string url, CancellationToken ct)
    {
        using HttpResponseMessage response = await client.GetAsync(url, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadJson(response, ct);
    }

    private static async Task<JsonElement> ReadJson(HttpResponseMessage response, CancellationToken ct)
    {
        string text = await response.Content.ReadAsStringAsync(ct);
        using JsonDocument doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    // U1: reverse-chron, limit honored + capped at 500, before cursor pages without
    // gap/overlap, nextBefore null at the end.
    [Fact]
    public async Task List_Pagination_ReverseChron_NoGapOrOverlap_LimitCapped()
    {
        await using TestVessel vessel = await TestVessel.StartAsync();
        using var client = new HttpClient();

        const int total = 7;
        for (int i = 0; i < total; i++)
        {
            using HttpResponseMessage r = await client.GetAsync($"{vessel.BaseUrl}/echo?i={i}", CT);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        }

        await CaptureDb.WaitUntil(vessel.DbPath, rows => rows.Count, count => count >= total);

        // limit is capped at 500 even when a caller asks for far more.
        JsonElement uncapped = await GetJson(client, $"{vessel.BaseUrl}/vessel/api/requests?limit=999999", CT);
        Assert.True(uncapped.GetProperty("rows").GetArrayLength() <= 500);

        var seenIds = new List<long>();
        long? cursor = null;
        for (int guard = 0; guard < total + 2 && (guard == 0 || cursor is not null); guard++)
        {
            string url = cursor is null
                ? $"{vessel.BaseUrl}/vessel/api/requests?limit=3"
                : $"{vessel.BaseUrl}/vessel/api/requests?limit=3&before={cursor}";
            JsonElement page = await GetJson(client, url, CT);
            foreach (JsonElement row in page.GetProperty("rows").EnumerateArray())
            {
                seenIds.Add(row.GetProperty("id").GetInt64());
            }

            JsonElement next = page.GetProperty("nextBefore");
            cursor = next.ValueKind == JsonValueKind.Null ? null : next.GetInt64();
        }

        Assert.Equal(total, seenIds.Count);
        Assert.Equal(seenIds, seenIds.OrderByDescending(x => x).ToList()); // reverse-chron
        Assert.Equal(seenIds.Distinct().Count(), seenIds.Count); // no overlap
    }

    // U2: decompressed bodies round-trip (UTF-8 -> text, binary -> base64); a streamed,
    // recognized-format row exposes both the reassembled responseBody and the raw
    // responseRaw; unknown id -> 404 not_found.
    [Fact]
    public async Task Detail_BodiesDecompressAndClassify_StreamedExposesBothBodies_UnknownId404()
    {
        await using TestVessel vessel = await TestVessel.StartAsync();
        using var client = new HttpClient();

        using var utf8Req = new HttpRequestMessage(HttpMethod.Post, $"{vessel.BaseUrl}/echo?utf8case")
        {
            Content = new StringContent("hello detail utf8", System.Text.Encoding.UTF8),
        };
        using HttpResponseMessage utf8Resp = await client.SendAsync(utf8Req, CT);
        Assert.Equal(HttpStatusCode.OK, utf8Resp.StatusCode);

        CapturedRow utf8Row = await CaptureDb.WaitForRow(vessel.DbPath, r => r.Path.Contains("utf8case"));
        JsonElement detail = await GetJson(client, $"{vessel.BaseUrl}/vessel/api/requests/{utf8Row.Id}", CT);
        Assert.Equal(utf8Row.Id, detail.GetProperty("id").GetInt64());
        JsonElement requestBody = detail.GetProperty("requestBody");
        Assert.Equal("hello detail utf8", requestBody.GetProperty("text").GetString());
        Assert.False(requestBody.TryGetProperty("base64", out _));

        byte[] binary = [0xC3, 0x28, 0xFF, 0x00, 0x01];
        using var binReq = new HttpRequestMessage(HttpMethod.Post, $"{vessel.BaseUrl}/echo?binarycase")
        {
            Content = new ByteArrayContent(binary),
        };
        using HttpResponseMessage binResp = await client.SendAsync(binReq, CT);
        Assert.Equal(HttpStatusCode.OK, binResp.StatusCode);
        CapturedRow binRow = await CaptureDb.WaitForRow(vessel.DbPath, r => r.Path.Contains("binarycase"));
        JsonElement binDetail = await GetJson(client, $"{vessel.BaseUrl}/vessel/api/requests/{binRow.Id}", CT);
        JsonElement binBody = binDetail.GetProperty("requestBody");
        Assert.Equal(Convert.ToBase64String(binary), binBody.GetProperty("base64").GetString());
        Assert.False(binBody.TryGetProperty("text", out _));

        // Streamed + recognized format (ollama-chat): reassembled responseBody and the raw
        // NDJSON responseRaw are both present and differ (one is folded, one is the wire).
        using HttpResponseMessage sseResp = await client.GetAsync($"{vessel.BaseUrl}/api/chat?stream=1&streamedcase", CT);
        Assert.Equal(HttpStatusCode.OK, sseResp.StatusCode);
        await sseResp.Content.ReadAsByteArrayAsync(CT);
        CapturedRow sseRow = await CaptureDb.WaitForRow(vessel.DbPath, r => r.Path.Contains("streamedcase"));
        JsonElement sseDetail = await GetJson(client, $"{vessel.BaseUrl}/vessel/api/requests/{sseRow.Id}", CT);
        Assert.True(sseDetail.GetProperty("streamed").GetBoolean());
        Assert.Equal("ollama-chat", sseDetail.GetProperty("format").GetString());

        string reassembled = sseDetail.GetProperty("responseBody").GetProperty("text").GetString()!;
        string raw = sseDetail.GetProperty("responseRaw").GetProperty("text").GetString()!;
        Assert.Contains("Hello", reassembled); // folded "He" + "llo"
        Assert.Contains("\"done\":false", raw); // wire NDJSON lines, not folded
        Assert.NotEqual(reassembled, raw);

        using HttpResponseMessage missing = await client.GetAsync($"{vessel.BaseUrl}/vessel/api/requests/999999999", CT);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("not_found", missing.Headers.GetValues("X-Vessel-Error").Single());
    }

    // U3: totals/averages over a seeded mix (failed = error-or->=400; avgTtftMs over
    // streamed rows only; session=current|id|all scoping).
    [Fact]
    public async Task Stats_TotalsAndAverages_SessionScoping()
    {
        await using TestVessel vessel = await TestVessel.StartAsync();
        using var client = new HttpClient();

        await client.GetAsync($"{vessel.BaseUrl}/echo?a", CT);
        await client.GetAsync($"{vessel.BaseUrl}/echo?b", CT);
        using HttpResponseMessage streamedResp = await client.GetAsync($"{vessel.BaseUrl}/sse?n=3&delayMs=30&c", CT);
        await streamedResp.Content.ReadAsByteArrayAsync(CT);
        using HttpResponseMessage failResp = await client.GetAsync($"{vessel.BaseUrl}/b/nope/echo?d", CT); // unknown backend -> error row
        Assert.Equal(HttpStatusCode.NotFound, failResp.StatusCode);

        await CaptureDb.WaitUntil(vessel.DbPath, rows => rows.Count, count => count >= 4);
        List<CapturedRow> rows = CaptureDb.Query(vessel.DbPath);
        double expectedAvgDuration = rows.Where(r => r.DurationMs is not null).Average(r => r.DurationMs!.Value);
        CapturedRow streamedRow = rows.Single(r => r.Streamed);

        JsonElement stats = await GetJson(client, $"{vessel.BaseUrl}/vessel/api/stats", CT); // default "current"
        Assert.Equal(4, stats.GetProperty("total").GetInt64());
        Assert.Equal(1, stats.GetProperty("failed").GetInt64());
        Assert.Equal(expectedAvgDuration, stats.GetProperty("avgDurationMs").GetDouble(), precision: 3);
        Assert.Equal(streamedRow.TtftMs!.Value, stats.GetProperty("avgTtftMs").GetDouble(), precision: 3); // the only streamed row
        // None of these 4 rows (echo/sse/unknown-backend) carry token data — the
        // null-safe-to-0 default (not absent, not null): a session with zero token
        // data is a genuine zero, not "not measured" (ui-spec.md §9.1).
        Assert.Equal(0, stats.GetProperty("tokensIn").GetInt64());
        Assert.Equal(0, stats.GetProperty("tokensOut").GetInt64());
        Assert.Equal(0, stats.GetProperty("tokensCachedRead").GetInt64());
        Assert.Equal(0, stats.GetProperty("tokensCachedWrite").GetInt64());
        Assert.False(stats.GetProperty("tokensEstimated").GetBoolean());
        long originalSessionId = stats.GetProperty("sessionId").GetInt64();
        Assert.False(string.IsNullOrEmpty(stats.GetProperty("sessionStartedAt").GetString()));

        // Reset -> new current session; its stats start at zero.
        using HttpResponseMessage resetResp = await client.PostAsync($"{vessel.BaseUrl}/vessel/api/sessions", null, CT);
        Assert.Equal(HttpStatusCode.Created, resetResp.StatusCode);
        JsonElement newSession = await ReadJson(resetResp, CT);
        long newSessionId = newSession.GetProperty("id").GetInt64();
        Assert.NotEqual(originalSessionId, newSessionId);

        JsonElement freshStats = await GetJson(client, $"{vessel.BaseUrl}/vessel/api/stats", CT);
        Assert.Equal(0, freshStats.GetProperty("total").GetInt64());
        Assert.Equal(newSessionId, freshStats.GetProperty("sessionId").GetInt64());

        JsonElement allStats = await GetJson(client, $"{vessel.BaseUrl}/vessel/api/stats?session=all", CT);
        Assert.Equal(4, allStats.GetProperty("total").GetInt64());
        Assert.Equal(JsonValueKind.Null, allStats.GetProperty("sessionId").ValueKind);

        JsonElement oldStats = await GetJson(client, $"{vessel.BaseUrl}/vessel/api/stats?session={originalSessionId}", CT);
        Assert.Equal(4, oldStats.GetProperty("total").GetInt64());
    }

    // Post-Phase-4 addition (ui-spec.md §9.1 token-totals TODO, phase-3.md D3): the
    // SUMs across multiple real rows. `tokensEstimated`'s per-row correctness (does
    // estimation actually flag a row) is already covered by FormatEnricherTests; this
    // proves the aggregation itself — scoping, column wiring — with exact, known
    // Ollama-reported counts (prompt_eval_count 5, eval_count 3 per call, fixed by the stub).
    [Fact]
    public async Task Stats_TokenTotals_SumAcrossRows()
    {
        await using TestVessel vessel = await TestVessel.StartAsync();
        using var client = new HttpClient();

        await client.GetAsync($"{vessel.BaseUrl}/api/chat?marker=one", CT);
        await client.GetAsync($"{vessel.BaseUrl}/api/chat?marker=two", CT);

        await CaptureDb.WaitUntil(vessel.DbPath, rows => rows.Count, count => count >= 2);

        JsonElement stats = await GetJson(client, $"{vessel.BaseUrl}/vessel/api/stats", CT);
        Assert.Equal(10, stats.GetProperty("tokensIn").GetInt64());
        Assert.Equal(6, stats.GetProperty("tokensOut").GetInt64());
        Assert.Equal(0, stats.GetProperty("tokensCachedRead").GetInt64());
        Assert.Equal(0, stats.GetProperty("tokensCachedWrite").GetInt64());
        Assert.False(stats.GetProperty("tokensEstimated").GetBoolean());
    }

    // U4: fresh DB auto-creates session 1; POST creates + returns a marker; a row started
    // before a reset keeps its original session_id even though it's written after (D4).
    [Fact]
    public async Task Sessions_FreshDbAutoCreatesSessionOne_PostCreatesMarker_InFlightRequestKeepsOriginalSession()
    {
        await using TestVessel vessel = await TestVessel.StartAsync();
        using var client = new HttpClient();

        JsonElement initial = await GetJson(client, $"{vessel.BaseUrl}/vessel/api/sessions", CT);
        Assert.Equal(1, initial.GetArrayLength());
        Assert.Equal("session 1", initial[0].GetProperty("name").GetString());
        long firstSessionId = initial[0].GetProperty("id").GetInt64();

        // Start a slow request that's still in flight when the reset happens.
        Task<HttpResponseMessage> inFlight = client.GetAsync($"{vessel.BaseUrl}/slow-headers?ms=800&inflight", CT);
        await Task.Delay(150, CT); // let it begin — CaptureContext is constructed, session captured

        using HttpResponseMessage resetResp = await client.PostAsJsonAsync(
            $"{vessel.BaseUrl}/vessel/api/sessions", new { name = "second" }, CT);
        Assert.Equal(HttpStatusCode.Created, resetResp.StatusCode);
        JsonElement created = await ReadJson(resetResp, CT);
        long secondSessionId = created.GetProperty("id").GetInt64();
        Assert.NotEqual(firstSessionId, secondSessionId);
        Assert.Equal("second", created.GetProperty("name").GetString());

        using HttpResponseMessage inFlightResp = await inFlight;
        Assert.Equal(HttpStatusCode.OK, inFlightResp.StatusCode);

        CapturedRow row = await CaptureDb.WaitForRow(vessel.DbPath, r => r.Path.Contains("inflight"));
        Assert.Equal(firstSessionId, row.SessionId); // D4: kept the session it started in

        await client.GetAsync($"{vessel.BaseUrl}/echo?afterreset", CT);
        CapturedRow afterRow = await CaptureDb.WaitForRow(vessel.DbPath, r => r.Path.Contains("afterreset"));
        Assert.Equal(secondSessionId, afterRow.SessionId);

        JsonElement sessions = await GetJson(client, $"{vessel.BaseUrl}/vessel/api/sessions", CT);
        Assert.Equal(2, sessions.GetArrayLength());
        Assert.Equal(secondSessionId, sessions[0].GetProperty("id").GetInt64()); // newest-first
    }

    // C1 (phase-4 carry-in): a non-numeric or overflowing id must 404, not 500.
    [Fact]
    public async Task Detail_NonNumericOrOverflowingId_404NotFound()
    {
        await using TestVessel vessel = await TestVessel.StartAsync();
        using var client = new HttpClient();

        using HttpResponseMessage nonNumeric = await client.GetAsync($"{vessel.BaseUrl}/vessel/api/requests/abc", CT);
        Assert.Equal(HttpStatusCode.NotFound, nonNumeric.StatusCode);
        Assert.Equal("not_found", nonNumeric.Headers.GetValues("X-Vessel-Error").Single());

        using HttpResponseMessage overflow = await client.GetAsync(
            $"{vessel.BaseUrl}/vessel/api/requests/99999999999999999999", CT);
        Assert.Equal(HttpStatusCode.NotFound, overflow.StatusCode);
        Assert.Equal("not_found", overflow.Headers.GetValues("X-Vessel-Error").Single());
    }

    // U7: unknown API path -> 404 JSON with X-Vessel-Error; /vessel/ without an embedded
    // dist -> a 200 placeholder, and it must never be the proxied backend's content.
    [Fact]
    public async Task UnknownApiPath_404Json_UiPathNeverProxied()
    {
        await using TestVessel vessel = await TestVessel.StartAsync();
        using var client = new HttpClient();

        using HttpResponseMessage apiResp = await client.GetAsync($"{vessel.BaseUrl}/vessel/api/nope", CT);
        Assert.Equal(HttpStatusCode.NotFound, apiResp.StatusCode);
        Assert.Equal("not_found", apiResp.Headers.GetValues("X-Vessel-Error").Single());
        Assert.Contains("\"vessel\"", await apiResp.Content.ReadAsStringAsync(CT));

        using HttpResponseMessage uiResp = await client.GetAsync($"{vessel.BaseUrl}/vessel/", CT);
        Assert.Equal(HttpStatusCode.OK, uiResp.StatusCode);
        Assert.False(uiResp.Headers.Contains("X-Vessel-Error"));
        string uiBody = await uiResp.Content.ReadAsStringAsync(CT);
        Assert.DoesNotContain("ServerId", uiBody); // never the stub's echo payload
    }
}
