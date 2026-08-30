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

    public string ConfigPath { get; private set; } = null!;

    /// <summary>The running app's DI container — for tests that need to reach a singleton directly.</summary>
    public IServiceProvider Services => _app.Services;

    /// <param name="firstRun">
    /// #11 — pretend this process created the config file, which is what arms the one-shot
    /// default-backend probe.
    /// </param>
    public static async Task<TestVessel> StartAsync(Action<VesselConfig>? mutate = null, bool firstRun = false)
    {
        var vessel = new TestVessel
        {
            _tempDir = Directory.CreateTempSubdirectory("vessel-tests-").FullName,
            Stub = await StubBackend.StartAsync("stub"),
        };
        vessel.DbPath = Path.Combine(vessel._tempDir, "vessel.db");
        vessel.ConfigPath = Path.Combine(vessel._tempDir, "vessel.json");

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

        vessel._app = VesselApp.Build(config, vessel.DbPath, vessel.ConfigPath, firstRun);
        await vessel._app.StartAsync();
        vessel._app.RecordBoundListen();
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
