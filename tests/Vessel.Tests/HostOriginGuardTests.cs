using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Vessel.Config;
using Xunit;

namespace Vessel.Tests;

/// <summary>
/// D03 — Host allowlist on <c>/vessel/*</c>, same-origin requirement on mutating
/// <c>/vessel/api/*</c>, proxied traffic untouched by either.
/// </summary>
public class HostOriginGuardTests
{
    private static CancellationToken CT => TestContext.Current.CancellationToken;

    [Fact]
    public async Task HostileHost_ControlPlaneGet_403()
    {
        await using TestVessel vessel = await TestVessel.StartAsync();
        using var client = new HttpClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{vessel.BaseUrl}/vessel/api/status");
        request.Headers.Host = "review.invalid";

        using HttpResponseMessage response = await client.SendAsync(request, CT);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("forbidden_host", response.Headers.GetValues("X-Vessel-Error").Single());
    }

    [Fact]
    public async Task HostileHost_EmbeddedUi_403()
    {
        await using TestVessel vessel = await TestVessel.StartAsync();
        using var client = new HttpClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{vessel.BaseUrl}/vessel/");
        request.Headers.Host = "review.invalid";

        using HttpResponseMessage response = await client.SendAsync(request, CT);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // Proxied traffic (the catch-all route, not /vessel/*) must never be affected by the
    // Host guard — SDK clients routinely send whatever Host their configured base URL has.
    [Fact]
    public async Task HostileHost_ProxiedRoute_Unaffected()
    {
        await using TestVessel vessel = await TestVessel.StartAsync();
        using var client = new HttpClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{vessel.BaseUrl}/echo?hostile");
        request.Headers.Host = "review.invalid";

        using HttpResponseMessage response = await client.SendAsync(request, CT);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    public async Task LoopbackHost_ControlPlaneGet_Allowed(string hostName)
    {
        await using TestVessel vessel = await TestVessel.StartAsync();
        using var client = new HttpClient();

        var authority = new Uri(vessel.BaseUrl);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{vessel.BaseUrl}/vessel/api/status");
        request.Headers.Host = $"{hostName}:{authority.Port}";

        using HttpResponseMessage response = await client.SendAsync(request, CT);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task CrossOriginPut_SecFetchSiteCrossSite_403()
    {
        await using TestVessel vessel = await TestVessel.StartAsync();
        using var client = new HttpClient();

        using var request = new HttpRequestMessage(HttpMethod.Put, $"{vessel.BaseUrl}/vessel/api/config")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Sec-Fetch-Site", "cross-site");

        using HttpResponseMessage response = await client.SendAsync(request, CT);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("forbidden_origin", response.Headers.GetValues("X-Vessel-Error").Single());
    }

    [Fact]
    public async Task CrossOriginPut_MismatchedOriginHeader_403()
    {
        await using TestVessel vessel = await TestVessel.StartAsync();
        using var client = new HttpClient();

        using var request = new HttpRequestMessage(HttpMethod.Put, $"{vessel.BaseUrl}/vessel/api/config")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Origin", "http://attacker.example");

        using HttpResponseMessage response = await client.SendAsync(request, CT);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("forbidden_origin", response.Headers.GetValues("X-Vessel-Error").Single());
    }

    [Fact]
    public async Task SameOriginPut_SecFetchSiteSameOrigin_NotRejectedByOriginGuard()
    {
        await using TestVessel vessel = await TestVessel.StartAsync();
        using var client = new HttpClient();

        VesselConfig candidate = await GetConfig(client, vessel.BaseUrl);
        using var request = new HttpRequestMessage(HttpMethod.Put, $"{vessel.BaseUrl}/vessel/api/config")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(candidate, ConfigJsonContext.Default.VesselConfig), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Sec-Fetch-Site", "same-origin");

        using HttpResponseMessage response = await client.SendAsync(request, CT);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task SameOriginPut_MatchingOriginHeader_NotRejectedByOriginGuard()
    {
        await using TestVessel vessel = await TestVessel.StartAsync();
        using var client = new HttpClient();

        VesselConfig candidate = await GetConfig(client, vessel.BaseUrl);
        using var request = new HttpRequestMessage(HttpMethod.Put, $"{vessel.BaseUrl}/vessel/api/config")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(candidate, ConfigJsonContext.Default.VesselConfig), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Origin", vessel.BaseUrl.TrimEnd('/'));

        using HttpResponseMessage response = await client.SendAsync(request, CT);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // A GET (read-only) is never subject to the same-origin check — only mutating verbs.
    [Fact]
    public async Task CrossSiteGet_NotRejectedByOriginGuard()
    {
        await using TestVessel vessel = await TestVessel.StartAsync();
        using var client = new HttpClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{vessel.BaseUrl}/vessel/api/config");
        request.Headers.Add("Sec-Fetch-Site", "cross-site");

        using HttpResponseMessage response = await client.SendAsync(request, CT);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<VesselConfig> GetConfig(HttpClient client, string baseUrl)
    {
        using HttpResponseMessage response = await client.GetAsync($"{baseUrl}/vessel/api/config", CT);
        string text = await response.Content.ReadAsStringAsync(CT);
        using JsonDocument doc = JsonDocument.Parse(text);
        return JsonSerializer.Deserialize(doc.RootElement.GetProperty("config").GetRawText(), ConfigJsonContext.Default.VesselConfig)!;
    }
}
