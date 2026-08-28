using System.Net;
using System.Text;
using System.Text.Json;
using Vessel.Api;
using Vessel.Config;
using Xunit;

namespace Vessel.Tests;

/// <summary>
/// Phase 4 D7 — <c>GET/PUT /vessel/api/config</c>: validation parity with startup, live
/// apply for new requests/next writer batch, <c>listen</c> as the one restart-required
/// field, and in-flight isolation from a concurrent PUT.
/// </summary>
public class ConfigApplyTests
{
    private static CancellationToken CT => TestContext.Current.CancellationToken;

    private static StringContent AsJson(VesselConfig config) =>
        new(JsonSerializer.Serialize(config, ConfigJsonContext.Default.VesselConfig), Encoding.UTF8, "application/json");

    private static async Task<VesselConfig> GetConfig(HttpClient client, string baseUrl) =>
        (await GetConfigResult(client, baseUrl)).Config;

    private static async Task<ConfigGetResult> GetConfigResult(HttpClient client, string baseUrl)
    {
        using HttpResponseMessage response = await client.GetAsync($"{baseUrl}/vessel/api/config", CT);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string text = await response.Content.ReadAsStringAsync(CT);
        return JsonSerializer.Deserialize(text, ApiJsonContext.Default.ConfigGetResult)!;
    }

    // V6: each invalid scenario -> 400 invalid_config, nothing persisted (no file written —
    // TestVessel never PUTs at startup, so "unchanged" here means "still absent"), GET
    // still returns the original config.
    [Fact]
    public async Task InvalidPut_DuplicateBackendNameCaseInsensitive_400_NothingApplied()
    {
        await using TestVessel vessel = await TestVessel.StartAsync();
        using var client = new HttpClient();

        VesselConfig candidate = await GetConfig(client, vessel.BaseUrl);
        candidate.Backends["Stub"] = new BackendConfig { BaseUrl = vessel.Stub.BaseUrl, Type = "auto" };

        using HttpResponseMessage response = await client.PutAsync($"{vessel.BaseUrl}/vessel/api/config", AsJson(candidate), CT);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_config", response.Headers.GetValues("X-Vessel-Error").Single());
        Assert.False(File.Exists(vessel.ConfigPath));

        VesselConfig afterGet = await GetConfig(client, vessel.BaseUrl);
        Assert.Single(afterGet.Backends);
    }

    [Fact]
    public async Task InvalidPut_BadUrl_400_NothingApplied()
    {
        await using TestVessel vessel = await TestVessel.StartAsync();
        using var client = new HttpClient();

        VesselConfig candidate = await GetConfig(client, vessel.BaseUrl);
        candidate.Backends["stub"].BaseUrl = "not-a-url";

        using HttpResponseMessage response = await client.PutAsync($"{vessel.BaseUrl}/vessel/api/config", AsJson(candidate), CT);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(File.Exists(vessel.ConfigPath));
    }

    [Fact]
    public async Task InvalidPut_NonPositiveRetention_400_NothingApplied()
    {
        await using TestVessel vessel = await TestVessel.StartAsync();
        using var client = new HttpClient();

        VesselConfig candidate = await GetConfig(client, vessel.BaseUrl);
        candidate.Retention.MaxRequests = 0;

        using HttpResponseMessage response = await client.PutAsync($"{vessel.BaseUrl}/vessel/api/config", AsJson(candidate), CT);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(File.Exists(vessel.ConfigPath));
    }

    [Fact]
    public async Task InvalidPut_UnknownDefaultBackend_400_NothingApplied()
    {
        await using TestVessel vessel = await TestVessel.StartAsync();
        using var client = new HttpClient();

        VesselConfig candidate = await GetConfig(client, vessel.BaseUrl);
        candidate.DefaultBackend = "does-not-exist";

        using HttpResponseMessage response = await client.PutAsync($"{vessel.BaseUrl}/vessel/api/config", AsJson(candidate), CT);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(File.Exists(vessel.ConfigPath));
    }

    // R15: a structurally-null section (`PUT {"backends":null}` and siblings) must 400 with
    // a human validation message, not a 500 from a NullReferenceException in Validate — and
    // must leave state/file exactly as InvalidPut_* above: no file, GET still the original.
    [Theory]
    [InlineData("""{ "backends": null }""")]
    [InlineData("""{ "backends": { "stub": null } }""")]
    [InlineData("""{ "retention": null }""")]
    [InlineData("""{ "capture": null }""")]
    [InlineData("""{ "warnings": null }""")]
    [InlineData("""{ "timeouts": null }""")]
    [InlineData("""{ "listen": null }""")]
    public async Task InvalidPut_NullSection_400_NothingApplied(string overrideJson)
    {
        await using TestVessel vessel = await TestVessel.StartAsync();
        using var client = new HttpClient();

        VesselConfig original = await GetConfig(client, vessel.BaseUrl);
        using JsonDocument baseDoc = JsonDocument.Parse(
            JsonSerializer.Serialize(original, ConfigJsonContext.Default.VesselConfig));
        using JsonDocument overrideDoc = JsonDocument.Parse(overrideJson);

        var merged = new Dictionary<string, JsonElement>();
        foreach (JsonProperty prop in baseDoc.RootElement.EnumerateObject())
        {
            merged[prop.Name] = prop.Value;
        }

        foreach (JsonProperty prop in overrideDoc.RootElement.EnumerateObject())
        {
            merged[prop.Name] = prop.Value;
        }

        using var content = new StringContent(JsonSerializer.Serialize(merged), Encoding.UTF8, "application/json");

        using HttpResponseMessage response = await client.PutAsync($"{vessel.BaseUrl}/vessel/api/config", content, CT);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_config", response.Headers.GetValues("X-Vessel-Error").Single());
        Assert.False(File.Exists(vessel.ConfigPath));

        VesselConfig afterGet = await GetConfig(client, vessel.BaseUrl);
        Assert.Equal(original.DefaultBackend, afterGet.DefaultBackend);
        Assert.Single(afterGet.Backends);
    }

    // V6: valid PUT rewrites vessel.json, preserving unknown properties (forward-compat,
    // same JsonExtensionData contract ConfigLoaderTests already proves for load/save).
    [Fact]
    public async Task ValidPut_PersistsToFile_UnknownPropertiesPreserved()
    {
        await using TestVessel vessel = await TestVessel.StartAsync();
        using var client = new HttpClient();

        string body =
            $$"""
            {
              "listen": "127.0.0.1:0",
              "defaultBackend": "stub",
              "backends": { "stub": { "baseUrl": "{{vessel.Stub.BaseUrl}}", "type": "auto", "futureBackendProp": 7 } },
              "retention": { "maxRequests": 10000, "maxDbSizeMb": 500 },
              "capture": { "maxBodyMb": 32 },
              "futureTopLevelProp": "keep-me"
            }
            """;
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        using HttpResponseMessage response = await client.PutAsync($"{vessel.BaseUrl}/vessel/api/config", content, CT);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(File.Exists(vessel.ConfigPath));

        using JsonDocument doc = JsonDocument.Parse(await File.ReadAllTextAsync(vessel.ConfigPath, CT));
        Assert.Equal("keep-me", doc.RootElement.GetProperty("futureTopLevelProp").GetString());
        Assert.Equal(7, doc.RootElement.GetProperty("backends").GetProperty("stub").GetProperty("futureBackendProp").GetInt32());
    }

    // V7: listen is the one restart-required field — still persisted and reflected by
    // GET, but the original listener keeps serving (no rebind).
    [Fact]
    public async Task ValidPut_ListenChange_ReportsRestartRequired_OldListenerStillServes()
    {
        await using TestVessel vessel = await TestVessel.StartAsync();
        using var client = new HttpClient();

        VesselConfig candidate = await GetConfig(client, vessel.BaseUrl);
        candidate.Listen = "127.0.0.1:59999";

        using HttpResponseMessage response = await client.PutAsync($"{vessel.BaseUrl}/vessel/api/config", AsJson(candidate), CT);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CT));
        Assert.True(doc.RootElement.GetProperty("applied").GetBoolean());
        string[] restartRequired = doc.RootElement.GetProperty("restartRequired").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Equal(["listen"], restartRequired);

        using HttpResponseMessage stillUp = await client.GetAsync($"{vessel.BaseUrl}/vessel/api/status", CT);
        Assert.Equal(HttpStatusCode.OK, stillUp.StatusCode);
    }

    // R16: the second save of the *same* changed listen value must still report the
    // restart as pending — comparing against the last-saved config (rather than the
    // address the process is actually bound to) made this silently report `[]` the
    // second time, even though the process never rebound.
    [Fact]
    public async Task ValidPut_RepeatedSaveOfSameListenChange_StillReportsRestartRequired()
    {
        await using TestVessel vessel = await TestVessel.StartAsync();
        using var client = new HttpClient();

        VesselConfig candidate = await GetConfig(client, vessel.BaseUrl);
        candidate.Listen = "127.0.0.1:59998";

        using HttpResponseMessage first = await client.PutAsync($"{vessel.BaseUrl}/vessel/api/config", AsJson(candidate), CT);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using JsonDocument firstDoc = JsonDocument.Parse(await first.Content.ReadAsStringAsync(CT));
        Assert.Equal(["listen"], firstDoc.RootElement.GetProperty("restartRequired").EnumerateArray().Select(e => e.GetString()!).ToArray());

        // An unrelated edit on top of the still-unapplied listen change.
        candidate.Retention.MaxRequests = 7;
        using HttpResponseMessage second = await client.PutAsync($"{vessel.BaseUrl}/vessel/api/config", AsJson(candidate), CT);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        using JsonDocument secondDoc = JsonDocument.Parse(await second.Content.ReadAsStringAsync(CT));
        Assert.Equal(["listen"], secondDoc.RootElement.GetProperty("restartRequired").EnumerateArray().Select(e => e.GetString()!).ToArray());

        // GET reflects the same still-pending state without a fresh PUT to remember it
        // (the R16 "shows it on reopen" half).
        ConfigGetResult reopened = await GetConfigResult(client, vessel.BaseUrl);
        Assert.Equal(["listen"], reopened.RestartRequired);

        // Reverting to the address the process is actually bound to clears it.
        candidate.Listen = new Uri(vessel.BaseUrl).Authority;
        using HttpResponseMessage revert = await client.PutAsync($"{vessel.BaseUrl}/vessel/api/config", AsJson(candidate), CT);
        Assert.Equal(HttpStatusCode.OK, revert.StatusCode);
        using JsonDocument revertDoc = JsonDocument.Parse(await revert.Content.ReadAsStringAsync(CT));
        Assert.Empty(revertDoc.RootElement.GetProperty("restartRequired").EnumerateArray());

        ConfigGetResult reopenedAfterRevert = await GetConfigResult(client, vessel.BaseUrl);
        Assert.Empty(reopenedAfterRevert.RestartRequired);
    }

    // V7: a non-listen field (here retention) PUT reports no restart required.
    [Fact]
    public async Task ValidPut_NonListenChange_ReportsNoRestartRequired()
    {
        await using TestVessel vessel = await TestVessel.StartAsync();
        using var client = new HttpClient();

        VesselConfig candidate = await GetConfig(client, vessel.BaseUrl);
        candidate.Retention.MaxRequests = 42;

        using HttpResponseMessage response = await client.PutAsync($"{vessel.BaseUrl}/vessel/api/config", AsJson(candidate), CT);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CT));
        Assert.Empty(doc.RootElement.GetProperty("restartRequired").EnumerateArray());
    }

    // V7: PUT redirecting a backend's baseUrl takes effect for the very next request —
    // no restart, no dropped requests during the swap.
    [Fact]
    public async Task ValidPut_BackendBaseUrlChange_AppliesLiveForNextRequest()
    {
        await using TestVessel vessel = await TestVessel.StartAsync();
        await using StubBackend secondStub = await StubBackend.StartAsync("second");
        using var client = new HttpClient();

        using HttpResponseMessage before = await client.GetAsync($"{vessel.BaseUrl}/echo?beforeswap", CT);
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        using JsonDocument beforeDoc = JsonDocument.Parse(await before.Content.ReadAsStringAsync(CT));
        Assert.Equal("stub", beforeDoc.RootElement.GetProperty("ServerId").GetString());

        VesselConfig candidate = await GetConfig(client, vessel.BaseUrl);
        candidate.Backends["stub"].BaseUrl = secondStub.BaseUrl;
        using HttpResponseMessage put = await client.PutAsync($"{vessel.BaseUrl}/vessel/api/config", AsJson(candidate), CT);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        using HttpResponseMessage after = await client.GetAsync($"{vessel.BaseUrl}/echo?afterswap", CT);
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        using JsonDocument afterDoc = JsonDocument.Parse(await after.Content.ReadAsStringAsync(CT));
        Assert.Equal("second", afterDoc.RootElement.GetProperty("ServerId").GetString());
    }

    // D7: retention re-reads the live config each batch — a PUT tightening maxRequests
    // is enforced on the very next writer batch, no restart.
    [Fact]
    public async Task ValidPut_RetentionChange_AppliesOnNextBatch()
    {
        await using TestVessel vessel = await TestVessel.StartAsync();
        using var client = new HttpClient();

        for (int i = 1; i <= 5; i++)
        {
            await client.GetAsync($"{vessel.BaseUrl}/echo?i={i}", CT);
        }

        await CaptureDb.WaitUntil(vessel.DbPath, rows => rows.Count, count => count >= 5);

        VesselConfig candidate = await GetConfig(client, vessel.BaseUrl);
        candidate.Retention.MaxRequests = 2;
        using HttpResponseMessage put = await client.PutAsync($"{vessel.BaseUrl}/vessel/api/config", AsJson(candidate), CT);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        // Retention runs after each writer batch — trigger one more so it re-evaluates.
        await client.GetAsync($"{vessel.BaseUrl}/echo?trigger", CT);

        List<CapturedRow> rows = await CaptureDb.WaitUntil(vessel.DbPath, r => r, r => r.Count <= 2);
        Assert.True(rows.Count <= 2);
    }

    // V8: a request already in flight keeps the ResolvedBackend it resolved at request
    // start (RouteResolver runs once, at Handle() entry) — a config PUT that repoints or
    // removes that backend mid-flight must not affect it.
    [Fact]
    public async Task InFlightRequest_UnaffectedByConcurrentConfigPut()
    {
        await using TestVessel vessel = await TestVessel.StartAsync();
        using var client = new HttpClient();

        Task<HttpResponseMessage> inFlight = client.GetAsync($"{vessel.BaseUrl}/slow-headers?ms=800&inflightconfig", CT);
        await Task.Delay(150, CT); // let it begin — backend already resolved for this request

        VesselConfig candidate = await GetConfig(client, vessel.BaseUrl);
        candidate.Backends["stub"].BaseUrl = "http://127.0.0.1:1"; // now points nowhere useful
        using HttpResponseMessage put = await client.PutAsync($"{vessel.BaseUrl}/vessel/api/config", AsJson(candidate), CT);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        using HttpResponseMessage inFlightResp = await inFlight;
        Assert.Equal(HttpStatusCode.OK, inFlightResp.StatusCode);
    }
}
