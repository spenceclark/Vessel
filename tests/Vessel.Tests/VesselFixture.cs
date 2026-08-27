using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Vessel.Config;
using Xunit;

namespace Vessel.Tests;

/// <summary>
/// Boots two stub backends ("alpha" = default, "beta"), a "dead" backend on a closed
/// port, and Vessel itself — all in-proc on random ports, plus a second Vessel with a
/// 1-second activity timeout for the timeout test.
/// </summary>
public sealed class VesselFixture : IAsyncLifetime
{
    public StubBackend Alpha { get; private set; } = null!;

    public StubBackend Beta { get; private set; } = null!;

    public string VesselBaseUrl { get; private set; } = null!;

    public string ShortTimeoutBaseUrl { get; private set; } = null!;

    public HttpClient Client { get; } = new();

    private WebApplication _vessel = null!;
    private WebApplication _shortTimeoutVessel = null!;

    public async ValueTask InitializeAsync()
    {
        Alpha = await StubBackend.StartAsync("alpha");
        Beta = await StubBackend.StartAsync("beta");

        var config = new VesselConfig
        {
            Listen = "127.0.0.1:0",
            DefaultBackend = "alpha",
            Backends = new Dictionary<string, BackendConfig>
            {
                ["alpha"] = new() { BaseUrl = Alpha.BaseUrl },
                ["beta"] = new() { BaseUrl = Beta.BaseUrl },
                ["dead"] = new() { BaseUrl = $"http://127.0.0.1:{ReserveClosedPort()}" },
            },
        };

        _vessel = VesselApp.Build(config);
        await _vessel.StartAsync();
        VesselBaseUrl = _vessel.ListenAddress();

        var shortTimeoutConfig = new VesselConfig
        {
            Listen = "127.0.0.1:0",
            DefaultBackend = "alpha",
            Backends = config.Backends,
            Timeouts = new TimeoutConfig { ActivitySeconds = 1 },
        };
        _shortTimeoutVessel = VesselApp.Build(shortTimeoutConfig);
        await _shortTimeoutVessel.StartAsync();
        ShortTimeoutBaseUrl = _shortTimeoutVessel.ListenAddress();
    }

    /// <summary>A loopback port that was just bound and released — connecting to it refuses.</summary>
    private static int ReserveClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _shortTimeoutVessel.DisposeAsync();
        await _vessel.DisposeAsync();
        await Beta.DisposeAsync();
        await Alpha.DisposeAsync();
    }
}
