using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Vessel.Config;
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

    [Fact]
    public async Task BackendBaseUrlPathPrefix_IsRetainedWhenForwarding()
    {
        await using TestVessel vessel = await TestVessel.StartAsync();
        var configStore = vessel.Services.GetRequiredService<ConfigStore>();
        VesselConfig config = configStore.Snapshot.Config;
        config.Backends["gemini"] = new BackendConfig
        {
            BaseUrl = $"{vessel.Stub.BaseUrl}/v1beta/openai",
            Type = "openai",
        };
        config.DefaultBackend = "gemini";
        configStore.Apply(config);

        using var client = new HttpClient();
        using HttpResponseMessage response = await client.GetAsync(
            $"{vessel.BaseUrl}/v1/responses?gemini-path-prefix", CT);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement body = await ReadJson(response, CT);
        Assert.Equal("/v1beta/openai/v1/responses", body.GetProperty("Path").GetString());
    }

    [Fact]
    public async Task Status_BackendHealth_IsPassiveAndReflectsCapturedOutcomes()
    {
        int deadPort = ReserveClosedPort();
        await using TestVessel vessel = await TestVessel.StartAsync(config =>
            config.Backends["dead"] = new BackendConfig { BaseUrl = $"http://127.0.0.1:{deadPort}" });
        using var client = new HttpClient();

        JsonElement initial = await GetJson(client, $"{vessel.BaseUrl}/vessel/api/status", CT);
        AssertHealth(initial, "stub", "unknown", expectedLastSeen: false);
        AssertHealth(initial, "dead", "unknown", expectedLastSeen: false);

        using HttpResponseMessage success = await client.GetAsync($"{vessel.BaseUrl}/echo", CT);
        Assert.Equal(HttpStatusCode.OK, success.StatusCode);
        JsonElement afterSuccess = await GetJson(client, $"{vessel.BaseUrl}/vessel/api/status", CT);
        AssertHealth(afterSuccess, "stub", "green", expectedLastSeen: true);

        // A backend-owned 4xx/5xx remains reachable; the request row itself reports its status.
        using HttpResponseMessage backendError = await client.GetAsync($"{vessel.BaseUrl}/respond", CT);
        Assert.Equal((HttpStatusCode)418, backendError.StatusCode);
        JsonElement afterBackendError = await GetJson(client, $"{vessel.BaseUrl}/vessel/api/status", CT);
        AssertHealth(afterBackendError, "stub", "green", expectedLastSeen: true);

        using HttpResponseMessage unreachable = await client.GetAsync($"{vessel.BaseUrl}/b/dead/echo", CT);
        Assert.Equal(HttpStatusCode.BadGateway, unreachable.StatusCode);
        JsonElement afterUnreachable = await GetJson(client, $"{vessel.BaseUrl}/vessel/api/status", CT);
        AssertHealth(afterUnreachable, "dead", "red", expectedLastSeen: true);
    }

    private static void AssertHealth(JsonElement status, string backendName, string state, bool expectedLastSeen)
    {
        JsonElement backend = status.GetProperty("backends").EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == backendName);
        JsonElement health = backend.GetProperty("health");
        Assert.Equal(state, health.GetProperty("state").GetString());
        Assert.Equal(expectedLastSeen ? JsonValueKind.String : JsonValueKind.Null, health.GetProperty("lastSeenAt").ValueKind);
    }

    private static int ReserveClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
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
        // R20: a fixed-decimal-place comparison here rounds two independently-computed
        // floating averages (SQL AVG vs. this LINQ re-aggregation of the same live-measured
        // values) before comparing, so ordinary summation-order noise near a rounding
        // boundary reads as a mismatch. A tolerance well above float noise but far below "a
        // real latency discrepancy" keeps this an integration check on live timing, not a
        // deterministic-precision check — Stats_SeededDurations_AverageMatchesExactly above
        // is that.
        Assert.True(
            Math.Abs(stats.GetProperty("avgDurationMs").GetDouble() - expectedAvgDuration) < 0.001,
            $"expected avgDurationMs near {expectedAvgDuration:R}, got {stats.GetProperty("avgDurationMs").GetDouble():R}");
        Assert.True(
            Math.Abs(stats.GetProperty("avgTtftMs").GetDouble() - streamedRow.TtftMs!.Value) < 0.001,
            $"expected avgTtftMs near {streamedRow.TtftMs.Value:R}, got {stats.GetProperty("avgTtftMs").GetDouble():R}"); // the only streamed row
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

    // R20 — the review reproduced an intermittent failure of the aggregate assertion above:
    // comparing SQLite's AVG(duration_ms) against a LINQ re-aggregation of the *same*
    // live-measured stored values landed on opposite sides of a 3-decimal rounding boundary
    // (18.820500000000003 vs 18.820499999999999 — ~4e-15 apart, ordinary floating-point
    // summation-order noise between two independent aggregation algorithms). This test seeds
    // exact, known durations directly into the store (bypassing the writer/proxy entirely,
    // so there is no live timing to vary run to run) and compares against a value computed
    // from those same literal seed constants — deterministic by construction, not merely
    // low-probability. The seeds deliberately don't divide evenly (600.7/3 has more binary
    // fractional digits than 3 decimal places can represent exactly) to keep exercising the
    // precision this coverage exists for; the comparison itself just no longer rounds both
    // sides to a fixed decimal place first, which is what turned harmless float noise into a
    // hard failure.
    [Fact]
    public async Task Stats_SeededDurations_AverageMatchesExactly()
    {
        await using TestVessel vessel = await TestVessel.StartAsync();
        using var client = new HttpClient();

        double[] seedDurations = [100.1, 200.2, 300.4];
        for (int i = 0; i < seedDurations.Length; i++)
        {
            CaptureDb.SeedRow(vessel.DbPath, $"2026-08-27T00:00:0{i}.0000000Z", seedDurations[i]);
        }

        double expectedAvg = seedDurations.Average();

        JsonElement stats = await GetJson(client, $"{vessel.BaseUrl}/vessel/api/stats?session=all", CT);
        Assert.Equal(3, stats.GetProperty("total").GetInt64());
        double actualAvg = stats.GetProperty("avgDurationMs").GetDouble();
        Assert.True(
            Math.Abs(actualAvg - expectedAvg) < 1e-9,
            $"expected avgDurationMs {expectedAvg:R} but got {actualAvg:R} (diff {Math.Abs(actualAvg - expectedAvg):R})");
    }

    // D01 — storage stays wire-true (phase-2 D3), so the detail endpoint is what makes a
    // compressed body readable. Both halves matter: the stored blob is still the gzip bytes,
    // and the API hands back decoded text.
    [Fact]
    public async Task Detail_CompressedBody_StoredWireTrue_DecodedForDisplay()
    {
        await using TestVessel vessel = await TestVessel.StartAsync();
        using var client = new HttpClient();

        // A chat-shaped request so format detection has something to sniff (the path is
        // deliberately non-standard — this is about the compressed *response* decoding).
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{vessel.BaseUrl}/gzip")
        {
            Content = new StringContent(
                """{"model":"gzip-model","messages":[{"role":"user","content":"hi"}]}""",
                Encoding.UTF8,
                "application/json"),
        };
        using HttpResponseMessage resp = await client.SendAsync(request, CT);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        await resp.Content.ReadAsByteArrayAsync(CT);

        await CaptureDb.WaitUntil(vessel.DbPath, rows => rows.Count, count => count >= 1);
        CapturedRow row = CaptureDb.Query(vessel.DbPath).Single(r => r.Path.StartsWith("/gzip"));

        // Wire-true storage: under the storage-level zstd, the bytes are still the gzip the
        // backend sent (magic 1f 8b), not decoded JSON.
        Assert.NotNull(row.ResponseBody);
        byte[] stored = CaptureDb.Decompress(row.ResponseBody);
        Assert.Equal(0x1f, stored[0]);
        Assert.Equal(0x8b, stored[1]);

        JsonElement detail = await GetJson(client, $"{vessel.BaseUrl}/vessel/api/requests/{row.Id}", CT);
        JsonElement body = detail.GetProperty("responseBody");
        Assert.Contains("compressed hello", body.GetProperty("text").GetString());
        // Nothing to flag: it fits the budget comfortably.
        Assert.False(body.TryGetProperty("decodeTruncated", out JsonElement flag) && flag.GetBoolean());

        // And the enricher parsed it from its own scratch decode, despite storage being wire bytes.
        Assert.Equal("gzip-model", detail.GetProperty("model").GetString());
        Assert.Equal(2, detail.GetProperty("tokensOut").GetInt64());
    }

    // R05 — a small wire body that expands past the capture budget must come back bounded
    // and explicitly flagged, never silently presented as the whole document.
    [Fact]
    public async Task Detail_DecodeExceedingBudget_IsBoundedAndFlagged()
    {
        await using TestVessel vessel = await TestVessel.StartAsync(c => c.Capture.MaxBodyMb = 1);
        using var client = new HttpClient();

        using HttpResponseMessage resp = await client.GetAsync($"{vessel.BaseUrl}/gzip?bomb=1", CT);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        await resp.Content.ReadAsByteArrayAsync(CT);

        await CaptureDb.WaitUntil(vessel.DbPath, rows => rows.Count, count => count >= 1);
        CapturedRow row = CaptureDb.Query(vessel.DbPath).Single(r => r.Path.StartsWith("/gzip"));

        // The wire body is tiny — the cap on captured bytes never noticed anything.
        Assert.NotNull(row.ResponseBody);
        Assert.True(row.ResponseBody!.Length < 64 * 1024, $"wire body should be small, was {row.ResponseBody.Length}");

        JsonElement detail = await GetJson(client, $"{vessel.BaseUrl}/vessel/api/requests/{row.Id}", CT);
        JsonElement body = detail.GetProperty("responseBody");

        Assert.True(body.GetProperty("decodeTruncated").GetBoolean(), "an over-budget decode must be flagged");

        // Bounded to the budget rather than the 4 MB the stream wanted to expand to.
        int shown = body.TryGetProperty("text", out JsonElement text)
            ? Encoding.UTF8.GetByteCount(text.GetString()!)
            : Convert.FromBase64String(body.GetProperty("base64").GetString()!).Length;
        Assert.True(shown <= 1024 * 1024, $"decoded display body should be bounded by the 1 MB budget, was {shown}");

        // The row itself carries the same fact the capture cap would have: the body is cut
        // off (phase-2 D3 treats a truncated capture and a truncated decode alike).
        Assert.Contains("body_truncated", row.WarningCodes);
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
        Assert.True(sessions[0].GetProperty("isCurrent").GetBoolean());
        Assert.False(sessions[1].GetProperty("isCurrent").GetBoolean());
    }

    [Fact]
    public async Task NamedSessionHeader_AssignsConcurrentRequestsPerName_WithoutChangingCurrentSession()
    {
        await using TestVessel vessel = await TestVessel.StartAsync();
        using var client = new HttpClient();

        JsonElement initialSessions = await GetJson(client, $"{vessel.BaseUrl}/vessel/api/sessions", CT);
        long currentSessionId = initialSessions[0].GetProperty("id").GetInt64();

        async Task Send(string name, string marker)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{vessel.BaseUrl}/api/chat?{marker}");
            request.Headers.TryAddWithoutValidation("X-Vessel-Session", name);
            using HttpResponseMessage response = await client.SendAsync(request, CT);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        await Task.WhenAll(Send("run-42", "agent-a"), Send("run-43", "agent-b"));
        await CaptureDb.WaitUntil(vessel.DbPath, rows => rows.Count, count => count >= 2);

        JsonElement sessions = await GetJson(client, $"{vessel.BaseUrl}/vessel/api/sessions", CT);
        JsonElement run42 = sessions.EnumerateArray().Single(s => s.GetProperty("name").GetString() == "run-42");
        JsonElement run43 = sessions.EnumerateArray().Single(s => s.GetProperty("name").GetString() == "run-43");
        Assert.Equal(1, run42.GetProperty("requestCount").GetInt64());
        Assert.Equal(1, run43.GetProperty("requestCount").GetInt64());
        Assert.Equal(JsonValueKind.String, run42.GetProperty("lastRequestAt").ValueKind);
        Assert.False(run42.GetProperty("isCurrent").GetBoolean());
        Assert.False(run43.GetProperty("isCurrent").GetBoolean());
        Assert.True(sessions.EnumerateArray().Single(s => s.GetProperty("id").GetInt64() == currentSessionId)
            .GetProperty("isCurrent").GetBoolean());

        IReadOnlyList<CapturedRow> rows = CaptureDb.Query(vessel.DbPath);
        Assert.Equal(run42.GetProperty("id").GetInt64(), rows.Single(r => r.Path.Contains("agent-a")).SessionId);
        Assert.Equal(run43.GetProperty("id").GetInt64(), rows.Single(r => r.Path.Contains("agent-b")).SessionId);

        JsonElement run42Stats = await GetJson(
            client, $"{vessel.BaseUrl}/vessel/api/stats?session={run42.GetProperty("id").GetInt64()}", CT);
        JsonElement run43Stats = await GetJson(
            client, $"{vessel.BaseUrl}/vessel/api/stats?session={run43.GetProperty("id").GetInt64()}", CT);
        JsonElement currentStats = await GetJson(
            client, $"{vessel.BaseUrl}/vessel/api/stats?session={currentSessionId}", CT);
        Assert.Equal(1, run42Stats.GetProperty("total").GetInt64());
        Assert.Equal(1, run43Stats.GetProperty("total").GetInt64());
        Assert.Equal(0, currentStats.GetProperty("total").GetInt64());

        // First sight creates; later requests with the exact same name reuse that marker.
        await Send("run-42", "agent-c");
        await CaptureDb.WaitUntil(vessel.DbPath, rs => rs.Count, count => count >= 3);
        JsonElement afterReuse = await GetJson(client, $"{vessel.BaseUrl}/vessel/api/sessions", CT);
        Assert.Equal(3, afterReuse.GetArrayLength());
        Assert.Equal(2, afterReuse.EnumerateArray().Single(s => s.GetProperty("name").GetString() == "run-42")
            .GetProperty("requestCount").GetInt64());
        JsonElement reusedStats = await GetJson(
            client, $"{vessel.BaseUrl}/vessel/api/stats?session={run42.GetProperty("id").GetInt64()}", CT);
        Assert.Equal(2, reusedStats.GetProperty("total").GetInt64());

        using HttpResponseMessage headerless = await client.GetAsync($"{vessel.BaseUrl}/api/chat?headerless", CT);
        Assert.Equal(HttpStatusCode.OK, headerless.StatusCode);
        await CaptureDb.WaitUntil(vessel.DbPath, rs => rs.Count, count => count >= 4);
        JsonElement currentAfterHeaderless = await GetJson(
            client, $"{vessel.BaseUrl}/vessel/api/stats?session={currentSessionId}", CT);
        Assert.Equal(1, currentAfterHeaderless.GetProperty("total").GetInt64());
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
