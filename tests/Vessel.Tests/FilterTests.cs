using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Vessel.Tests;

/// <summary>
/// Phase 4 D1/D2 — list filters (q/backend/model/format/tag/status/warned) and facets.
/// Each test gets its own fresh <see cref="VesselFixture"/> (not <c>IClassFixture</c>) so
/// row counts are exact, not polluted by other tests sharing one DB — same reasoning as
/// <see cref="ApiTests"/>'s per-test <see cref="TestVessel"/>. Backend-diversity filters
/// need alpha/beta/dead, which only <see cref="VesselFixture"/> provides.
/// </summary>
public class FilterTests
{
    private static CancellationToken CT => TestContext.Current.CancellationToken;

    private static async Task<VesselFixture> NewFixtureAsync()
    {
        var fx = new VesselFixture();
        await fx.InitializeAsync();
        return fx;
    }

    private static async Task<JsonElement> ListAsync(VesselFixture fx, string query)
    {
        using HttpResponseMessage response = await fx.Client.GetAsync($"{fx.VesselBaseUrl}/vessel/api/requests?{query}", CT);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string text = await response.Content.ReadAsStringAsync(CT);
        using JsonDocument doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    private static long[] RowIds(JsonElement listResponse) =>
        listResponse.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("id").GetInt64()).ToArray();

    [Fact]
    public async Task Backend_ExactCaseInsensitive()
    {
        await using VesselFixture fx = await NewFixtureAsync();

        using HttpResponseMessage alphaResp = await fx.Client.GetAsync($"{fx.VesselBaseUrl}/echo?alphacase", CT);
        Assert.Equal(HttpStatusCode.OK, alphaResp.StatusCode);
        using HttpResponseMessage betaResp = await fx.Client.GetAsync($"{fx.VesselBaseUrl}/b/beta/echo?betacase", CT);
        Assert.Equal(HttpStatusCode.OK, betaResp.StatusCode);

        CapturedRow betaRow = await CaptureDb.WaitForRow(fx.DbPath, r => r.Path.Contains("betacase"));
        await CaptureDb.WaitForRow(fx.DbPath, r => r.Path.Contains("alphacase"));

        // Case-insensitive: "BETA" must still match the "beta" backend.
        JsonElement page = await ListAsync(fx, "backend=BETA");
        Assert.Equal([betaRow.Id], RowIds(page));
    }

    [Fact]
    public async Task Model_Exact()
    {
        await using VesselFixture fx = await NewFixtureAsync();

        await fx.Client.GetAsync($"{fx.VesselBaseUrl}/api/chat?marker=x&model=model-a", CT);
        await fx.Client.GetAsync($"{fx.VesselBaseUrl}/api/chat?marker=y&model=model-b", CT);
        await CaptureDb.WaitUntil(fx.DbPath, rows => rows.Count, count => count >= 2);

        CapturedRow expected = await CaptureDb.WaitForRow(fx.DbPath, r => r.Model == "model-a");

        JsonElement page = await ListAsync(fx, "model=model-a");
        Assert.Equal([expected.Id], RowIds(page));
    }

    [Fact]
    public async Task Format_Exact_RawIncluded()
    {
        await using VesselFixture fx = await NewFixtureAsync();

        await fx.Client.GetAsync($"{fx.VesselBaseUrl}/echo?formatraw", CT);
        await fx.Client.GetAsync($"{fx.VesselBaseUrl}/api/chat?marker=formatchat", CT);

        CapturedRow rawRow = await CaptureDb.WaitForRow(fx.DbPath, r => r.Path.Contains("formatraw"));
        CapturedRow chatRow = await CaptureDb.WaitForRow(fx.DbPath, r => r.Path.Contains("formatchat"));

        Assert.Equal([rawRow.Id], RowIds(await ListAsync(fx, "format=raw")));
        Assert.Equal([chatRow.Id], RowIds(await ListAsync(fx, "format=ollama-chat")));
    }

    [Fact]
    public async Task Tag_ExactElementMatch_NeverSubstring()
    {
        await using VesselFixture fx = await NewFixtureAsync();

        using var taggedA = new HttpRequestMessage(HttpMethod.Get, $"{fx.VesselBaseUrl}/echo?tagacase");
        taggedA.Headers.TryAddWithoutValidation("X-Vessel-Tags", "a");
        await fx.Client.SendAsync(taggedA, CT);

        using var taggedAbc = new HttpRequestMessage(HttpMethod.Get, $"{fx.VesselBaseUrl}/echo?tagabccase");
        taggedAbc.Headers.TryAddWithoutValidation("X-Vessel-Tags", "abc");
        await fx.Client.SendAsync(taggedAbc, CT);

        CapturedRow rowA = await CaptureDb.WaitForRow(fx.DbPath, r => r.Path.Contains("tagacase"));
        await CaptureDb.WaitForRow(fx.DbPath, r => r.Path.Contains("tagabccase"));

        JsonElement page = await ListAsync(fx, "tag=a");
        Assert.Equal([rowA.Id], RowIds(page));
    }

    [Fact]
    public async Task Status_OkAndError()
    {
        await using VesselFixture fx = await NewFixtureAsync();

        using HttpResponseMessage okResp = await fx.Client.GetAsync($"{fx.VesselBaseUrl}/echo?statusok", CT);
        Assert.Equal(HttpStatusCode.OK, okResp.StatusCode);
        using HttpResponseMessage errResp = await fx.Client.GetAsync($"{fx.VesselBaseUrl}/b/dead/echo?statuserr", CT);
        Assert.Equal(HttpStatusCode.BadGateway, errResp.StatusCode);

        CapturedRow okRow = await CaptureDb.WaitForRow(fx.DbPath, r => r.Path.Contains("statusok"));
        CapturedRow errRow = await CaptureDb.WaitForRow(fx.DbPath, r => r.Path.Contains("statuserr"));

        Assert.Equal([okRow.Id], RowIds(await ListAsync(fx, "status=ok")));
        Assert.Equal([errRow.Id], RowIds(await ListAsync(fx, "status=error")));
    }

    [Fact]
    public async Task Warned_OnlyWarningsPresent()
    {
        await using VesselFixture fx = await NewFixtureAsync();

        await fx.Client.GetAsync($"{fx.VesselBaseUrl}/echo?warnok", CT);
        using HttpResponseMessage errResp = await fx.Client.GetAsync($"{fx.VesselBaseUrl}/b/dead/echo?warnerr", CT);
        Assert.Equal(HttpStatusCode.BadGateway, errResp.StatusCode);

        await CaptureDb.WaitForRow(fx.DbPath, r => r.Path.Contains("warnok"));
        CapturedRow warnedRow = await CaptureDb.WaitForRow(fx.DbPath, r => r.Path.Contains("warnerr"));
        Assert.NotEmpty(warnedRow.WarningCodes);

        JsonElement page = await ListAsync(fx, "warned=1");
        Assert.Equal([warnedRow.Id], RowIds(page));
    }

    // V3: a three-way combination (backend + status=error + warned=1) must be a strict
    // AND, not an OR — the /respond decoy matches status=error and warned=1 (http_error,
    // 418) but a different backend, and must be excluded.
    [Fact]
    public async Task ThreeWayCombination_Backend_StatusError_Warned_IsStrictAnd()
    {
        await using VesselFixture fx = await NewFixtureAsync();

        using HttpResponseMessage deadResp = await fx.Client.GetAsync($"{fx.VesselBaseUrl}/b/dead/echo?threeway", CT);
        Assert.Equal(HttpStatusCode.BadGateway, deadResp.StatusCode);
        using HttpResponseMessage decoyResp = await fx.Client.GetAsync($"{fx.VesselBaseUrl}/respond?respdecoy", CT);
        Assert.Equal((HttpStatusCode)418, decoyResp.StatusCode);
        await fx.Client.GetAsync($"{fx.VesselBaseUrl}/echo?plainrow", CT);

        CapturedRow matchRow = await CaptureDb.WaitForRow(fx.DbPath, r => r.Path.Contains("threeway"));
        CapturedRow decoyRow = await CaptureDb.WaitForRow(fx.DbPath, r => r.Path.Contains("respdecoy"));
        Assert.Equal("dead", matchRow.Backend);
        Assert.Equal("alpha", decoyRow.Backend);
        Assert.NotEmpty(decoyRow.WarningCodes); // matches status=error + warned=1, but wrong backend

        JsonElement page = await ListAsync(fx, "backend=dead&status=error&warned=1");
        Assert.Equal([matchRow.Id], RowIds(page));
    }

    [Fact]
    public async Task Fts_MatchesPromptAndResponseText()
    {
        await using VesselFixture fx = await NewFixtureAsync();

        using var promptReq = new HttpRequestMessage(HttpMethod.Post, $"{fx.VesselBaseUrl}/api/chat?promptcase")
        {
            Content = new StringContent(
                """{"model":"stub-model","messages":[{"role":"user","content":"zzpromptneedle"}]}""",
                Encoding.UTF8, "application/json"),
        };
        await fx.Client.SendAsync(promptReq, CT);
        await fx.Client.GetAsync($"{fx.VesselBaseUrl}/api/chat?marker=zzresponseneedle", CT);

        CapturedRow promptRow = await CaptureDb.WaitForRow(fx.DbPath, r => r.Path.Contains("promptcase"));
        CapturedRow responseRow = await CaptureDb.WaitForRow(fx.DbPath, r => r.Path.Contains("marker=zzresponseneedle"));

        Assert.Equal([promptRow.Id], RowIds(await ListAsync(fx, "q=zzpromptneedle")));
        Assert.Equal([responseRow.Id], RowIds(await ListAsync(fx, "q=zzresponseneedle")));
    }

    // V2: hostile input must never surface an FTS syntax error — every operator becomes
    // literal text once quoted.
    [Fact]
    public async Task Fts_HostileInput_NeverErrors()
    {
        await using VesselFixture fx = await NewFixtureAsync();
        await fx.Client.GetAsync($"{fx.VesselBaseUrl}/echo?hostilebaseline", CT);
        await CaptureDb.WaitUntil(fx.DbPath, rows => rows.Count, count => count >= 1);

        using HttpResponseMessage response = await fx.Client.GetAsync(
            $"{fx.VesselBaseUrl}/vessel/api/requests?q=" + Uri.EscapeDataString("\"foo\" AND (bar* NEAR"), CT);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // V2: q composes with cursor paging — no gap or overlap across pages, all matches found.
    [Fact]
    public async Task Fts_ComposesWithCursorPaging_NoGapOrOverlap()
    {
        await using VesselFixture fx = await NewFixtureAsync();

        const int total = 7;
        for (int i = 0; i < total; i++)
        {
            await fx.Client.GetAsync($"{fx.VesselBaseUrl}/api/chat?marker=ftspagingword&i={i}", CT);
        }

        await CaptureDb.WaitUntil(fx.DbPath, rows => rows.Count(r => r.Path.Contains("ftspagingword")), c => c >= total);

        var seenIds = new List<long>();
        long? cursor = null;
        for (int guard = 0; guard < total + 2 && (guard == 0 || cursor is not null); guard++)
        {
            string query = cursor is null
                ? "q=ftspagingword&limit=3"
                : $"q=ftspagingword&limit=3&before={cursor}";
            JsonElement page = await ListAsync(fx, query);
            seenIds.AddRange(RowIds(page));
            JsonElement next = page.GetProperty("nextBefore");
            cursor = next.ValueKind == JsonValueKind.Null ? null : next.GetInt64();
        }

        Assert.Equal(total, seenIds.Count);
        Assert.Equal(seenIds.Distinct().Count(), seenIds.Count);
    }

    [Fact]
    public async Task Facets_ScopedDistinctCappedAlphabetical()
    {
        await using VesselFixture fx = await NewFixtureAsync();

        await fx.Client.GetAsync($"{fx.VesselBaseUrl}/api/chat?marker=x&model=zeta-model", CT);
        await fx.Client.GetAsync($"{fx.VesselBaseUrl}/api/chat?marker=y&model=alpha-model", CT);
        using var tagged = new HttpRequestMessage(HttpMethod.Get, $"{fx.VesselBaseUrl}/echo?facettag");
        tagged.Headers.TryAddWithoutValidation("X-Vessel-Tags", "facet-tag");
        await fx.Client.SendAsync(tagged, CT);

        await CaptureDb.WaitUntil(fx.DbPath, rows => rows.Count, count => count >= 3);

        using HttpResponseMessage response = await fx.Client.GetAsync($"{fx.VesselBaseUrl}/vessel/api/requests/facets", CT);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CT));
        JsonElement facets = doc.RootElement;

        string[] models = facets.GetProperty("models").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Contains("zeta-model", models);
        Assert.Contains("alpha-model", models);
        Assert.Equal(models.OrderBy(m => m, StringComparer.Ordinal), models); // alphabetical

        string[] tags = facets.GetProperty("tags").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Contains("facet-tag", tags);

        string[] backends = facets.GetProperty("backends").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Contains("alpha", backends);
    }
}
