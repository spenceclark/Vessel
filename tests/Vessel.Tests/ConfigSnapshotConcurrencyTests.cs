using Vessel.Capture;
using Vessel.Config;
using Vessel.Formats;
using Vessel.Proxy;
using Xunit;

namespace Vessel.Tests;

/// <summary>
/// R02 — config revision and derived state must be observed as one value. The original
/// defect: <c>BackendRegistry</c> read <c>Current</c>, built its map, then read
/// <c>Version</c> separately, so a PUT landing between those reads labelled revision N's
/// map with revision N+1 and left routing stale until the *next* PUT.
/// </summary>
public class ConfigSnapshotConcurrencyTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("vessel-cfgsnap-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string ConfigPath => Path.Combine(_dir, "vessel.json");

    private static VesselConfig ConfigWith(string baseUrl) => new()
    {
        Listen = "127.0.0.1:4550",
        DefaultBackend = "b",
        Backends = new Dictionary<string, BackendConfig>
        {
            ["b"] = new() { BaseUrl = baseUrl, Type = "openai" },
        },
    };

    /// <summary>
    /// The invariant the review's probe violated: whatever revision a caller resolves
    /// against, the backends it gets back are *that revision's* backends. Checked on every
    /// iteration of interleaved saves and lookups — any single mismatch fails.
    /// </summary>
    [Fact]
    public async Task ConcurrentApply_ResolvedBackendsAlwaysMatchTheirSnapshot()
    {
        var store = new ConfigStore(ConfigWith("http://127.0.0.1:11000"), ConfigPath);
        var registry = new BackendRegistry(store);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        var mismatches = new List<string>();
        var readerFaulted = new List<Exception>();

        Task writer = Task.Run(() =>
        {
            for (int i = 1; i <= 150 && !cts.IsCancellationRequested; i++)
            {
                store.Apply(ConfigWith($"http://127.0.0.1:{11000 + i}"));
            }
        }, cts.Token);

        Task[] readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            try
            {
                while (!writer.IsCompleted && !cts.IsCancellationRequested)
                {
                    // Exactly the proxy path's shape: one snapshot, then resolve against it.
                    ConfigSnapshot snapshot = store.Snapshot;
                    BackendSet backends = registry.Resolve(snapshot);

                    string expected = snapshot.Config.Backends["b"].BaseUrl;
                    string? actual = backends.Find("b")?.BaseUrl;
                    if (actual != expected)
                    {
                        lock (mismatches)
                        {
                            mismatches.Add($"snapshot v{snapshot.Version} says {expected}, lookup returned {actual}");
                        }
                    }

                    // The default must come from the same revision as the map.
                    if (backends.Default.BaseUrl != expected)
                    {
                        lock (mismatches)
                        {
                            mismatches.Add($"snapshot v{snapshot.Version} says {expected}, default returned {backends.Default.BaseUrl}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                lock (readerFaulted) { readerFaulted.Add(ex); }
            }
        }, cts.Token)).ToArray();

        await writer;
        await Task.WhenAll(readers);

        Assert.Empty(readerFaulted);
        Assert.Empty(mismatches);
    }

    /// <summary>
    /// The user-visible half of R02: after the last save settles, a lookup with no snapshot
    /// of its own reflects the final config. Staleness must not survive the race.
    /// </summary>
    [Fact]
    public async Task AfterConcurrentApplies_LatestLookupReflectsFinalConfig()
    {
        var store = new ConfigStore(ConfigWith("http://127.0.0.1:11000"), ConfigPath);
        var registry = new BackendRegistry(store);

        Task readerLoop = Task.Run(
            () =>
            {
                for (int i = 0; i < 20_000; i++)
                {
                    _ = registry.Find("b");
                }
            },
            TestContext.Current.CancellationToken);

        for (int i = 1; i <= 50; i++)
        {
            store.Apply(ConfigWith($"http://127.0.0.1:{11000 + i}"));
        }

        await readerLoop;

        Assert.Equal("http://127.0.0.1:11050", registry.Find("b")?.BaseUrl);
        Assert.Equal("http://127.0.0.1:11050", registry.Default.BaseUrl);
        Assert.Equal(50, store.Snapshot.Version);
    }

    /// <summary>
    /// The enricher has the same read-twice shape and the same fix; it runs on the single
    /// writer thread, so this pins the sequential contract rather than a race: derived state
    /// tracks the snapshot, and a revision is never skipped.
    /// </summary>
    [Fact]
    public void Enricher_TracksSnapshotAcrossApplies()
    {
        // The backend name must match TestCapture's ("test"): the backend-type tiebreak is
        // keyed by the record's backend, and that lookup is the derived state under test.
        static VesselConfig WithType(string type) => new()
        {
            Listen = "127.0.0.1:4550",
            DefaultBackend = "test",
            Backends = new Dictionary<string, BackendConfig>
            {
                ["test"] = new() { BaseUrl = "http://127.0.0.1:11000", Type = type },
            },
        };

        var store = new ConfigStore(WithType("openai"), ConfigPath);
        var enricher = new FormatEnricher(store, FormatEnricher.DefaultAdapters());

        // A prefix-less path with a chat-shaped request and no response resolves only via
        // the backend-type tiebreak, so the format tracks the config revision.
        CaptureRecord Chat() => TestCapture.Record(
            "/nonstandard", """{"model":"m","messages":[{"role":"user","content":"hi"}]}""", null);

        Assert.Equal(FormatNames.OpenAiChat, enricher.Enrich(Chat()).Format);

        store.Apply(WithType("ollama"));

        Assert.Equal(FormatNames.OllamaChat, enricher.Enrich(Chat()).Format);
    }
}
