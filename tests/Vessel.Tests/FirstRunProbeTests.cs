using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Vessel.Config;
using Vessel.Proxy;
using Xunit;

namespace Vessel.Tests;

/// <summary>
/// Issue #11 — the default backend stays Ollama, so a machine without Ollama has a dead
/// default that nothing announces until a client collects a <c>502 upstream_unreachable</c>.
/// Passive health can't answer before any traffic exists, so a first run — and only a first
/// run — asks once, and <c>/vessel/api/status</c> carries the answer for the UI.
/// </summary>
public sealed class FirstRunProbeTests
{
    private static CancellationToken CT => TestContext.Current.CancellationToken;

    [Fact]
    public async Task IsReachableAsync_AnswersFromWhetherAnythingIsListening()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string baseUrl = $"http://127.0.0.1:{port}";

        try
        {
            Assert.True(await BackendProbe.IsReachableAsync(baseUrl, TimeSpan.FromSeconds(5), CT));
        }
        finally
        {
            listener.Stop();
        }

        Assert.False(await BackendProbe.IsReachableAsync(baseUrl, TimeSpan.FromSeconds(5), CT));
    }

    // The probe must never be able to reach something that could bill for it, so it reuses
    // config validation's own definition of "can't leave this machine or its LAN" (#5).
    [Theory]
    [InlineData("http://localhost:11434", true)]
    [InlineData("http://127.0.0.1:11434", true)]
    [InlineData("http://192.168.1.20:11434", true)]
    [InlineData("http://host.docker.internal:11434", true)]
    [InlineData("https://api.openai.com", false)]
    [InlineData("https://api.anthropic.com", false)]
    [InlineData("https://generativelanguage.googleapis.com/v1beta/openai", false)]
    [InlineData("ollama.example.com:11434", false)]
    public void IsProbeable_OnlyEverTargetsHostsThatCannotBeAPaidApi(string baseUrl, bool probeable) =>
        Assert.Equal(probeable, BackendProbe.IsProbeable(baseUrl));

    [Fact]
    public async Task Status_ReportsNoProbeAtAll_OnEveryRunButTheFirst()
    {
        await using TestVessel vessel = await TestVessel.StartAsync();

        JsonElement setup = await GetSetup(vessel);
        Assert.False(setup.GetProperty("firstRun").GetBoolean());
        Assert.Equal(JsonValueKind.Null, setup.GetProperty("defaultBackendReachable").ValueKind);
    }

    [Fact]
    public async Task Status_FirstRun_ReportsTheDefaultBackendReachable_WhenItIsListening()
    {
        await using TestVessel vessel = await TestVessel.StartAsync(firstRun: true);

        JsonElement setup = await GetSetup(vessel);
        Assert.True(setup.GetProperty("firstRun").GetBoolean());
        Assert.True(setup.GetProperty("defaultBackendReachable").GetBoolean());
    }

    [Fact]
    public async Task Status_FirstRun_ReportsTheDefaultBackendUnreachable_WhenNothingAnswers()
    {
        int deadPort = ReserveClosedPort();
        await using TestVessel vessel = await TestVessel.StartAsync(
            config => config.Backends["stub"] = new BackendConfig { BaseUrl = $"http://127.0.0.1:{deadPort}" },
            firstRun: true);

        JsonElement setup = await GetSetup(vessel);
        Assert.True(setup.GetProperty("firstRun").GetBoolean());
        Assert.False(setup.GetProperty("defaultBackendReachable").GetBoolean());

        // The probe is not a health source: the dots stay passive, so a backend no request
        // has ever been sent to is still "unknown", not red.
        JsonElement backend = (await GetStatus(vessel)).GetProperty("backends").EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "stub");
        Assert.Equal("unknown", backend.GetProperty("health").GetProperty("state").GetString());
    }

    private static async Task<JsonElement> GetSetup(TestVessel vessel) =>
        (await GetStatus(vessel)).GetProperty("setup");

    private static async Task<JsonElement> GetStatus(TestVessel vessel)
    {
        using var client = new HttpClient();
        using HttpResponseMessage response = await client.GetAsync($"{vessel.BaseUrl}/vessel/api/status", CT);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CT));
        return doc.RootElement.Clone();
    }

    private static int ReserveClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
