using Microsoft.AspNetCore.Builder;
using Vessel.Config;

namespace Vessel.Tests;

/// <summary>
/// A self-contained Vessel + single stub backend ("stub", default) with a per-instance
/// temp database — for tests that need non-default config (tiny caps, retention).
/// </summary>
public sealed class TestVessel : IAsyncDisposable
{
    private string _tempDir = null!;
    private WebApplication _app = null!;

    public StubBackend Stub { get; private set; } = null!;

    public string BaseUrl { get; private set; } = null!;

    public string DbPath { get; private set; } = null!;

    public static async Task<TestVessel> StartAsync(Action<VesselConfig>? mutate = null)
    {
        var vessel = new TestVessel
        {
            _tempDir = Directory.CreateTempSubdirectory("vessel-tests-").FullName,
            Stub = await StubBackend.StartAsync("stub"),
        };
        vessel.DbPath = Path.Combine(vessel._tempDir, "vessel.db");

        var config = new VesselConfig
        {
            Listen = "127.0.0.1:0",
            DefaultBackend = "stub",
            Backends = new Dictionary<string, BackendConfig>
            {
                ["stub"] = new() { BaseUrl = vessel.Stub.BaseUrl },
            },
        };
        mutate?.Invoke(config);

        vessel._app = VesselApp.Build(config, vessel.DbPath);
        await vessel._app.StartAsync();
        vessel.BaseUrl = vessel._app.ListenAddress();
        return vessel;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        await Stub.DisposeAsync();
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
