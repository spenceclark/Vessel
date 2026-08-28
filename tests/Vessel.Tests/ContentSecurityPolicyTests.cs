using System.Net;
using Xunit;

namespace Vessel.Tests;

/// <summary>
/// R03/R18 — a strict CSP on <c>/vessel/*</c> responses as defense in depth alongside the
/// frontend's own resource policy (captured content never emits a live fetchable
/// src/href). UI routes only: proxied backend responses must never carry it.
/// </summary>
public class ContentSecurityPolicyTests
{
    private static CancellationToken CT => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("/vessel/api/status")]
    [InlineData("/vessel/")]
    public async Task VesselRoutes_CarryStrictCsp(string path)
    {
        await using TestVessel vessel = await TestVessel.StartAsync();
        using var client = new HttpClient();

        using HttpResponseMessage response = await client.GetAsync($"{vessel.BaseUrl}{path}", CT);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string csp = response.Headers.GetValues("Content-Security-Policy").Single();
        string[] directives = csp.Split(';', StringSplitOptions.TrimEntries);
        Assert.Contains("default-src 'self'", directives);
        Assert.Contains("img-src 'self' data: blob:", directives);
        Assert.Contains("script-src 'self'", directives); // no unsafe-inline/unsafe-eval on script-src
    }

    // Proxied traffic must never carry the UI's CSP — that would apply Vessel's own
    // resource policy to whatever the backend actually returns, which is not Vessel's
    // response to police.
    [Fact]
    public async Task ProxiedRoute_NoCsp()
    {
        await using TestVessel vessel = await TestVessel.StartAsync();
        using var client = new HttpClient();

        using HttpResponseMessage response = await client.GetAsync($"{vessel.BaseUrl}/echo?csp", CT);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Content-Security-Policy"));
    }
}
