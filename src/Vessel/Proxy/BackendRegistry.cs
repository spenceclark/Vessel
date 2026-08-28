using Vessel.Config;

namespace Vessel.Proxy;

/// <summary>A configured backend with its name resolved and base URL normalized.</summary>
public sealed record ResolvedBackend(string Name, string BaseUrl, string Type, bool IsDefault, bool InjectStreamUsage);

/// <summary>
/// R02 — the backends of exactly one <see cref="ConfigSnapshot"/>, resolved together. The
/// lookup map and the default are one immutable value, so a reader can never observe a new
/// map paired with the previous default.
/// </summary>
public sealed class BackendSet
{
    private readonly Dictionary<string, ResolvedBackend> _byName;

    internal BackendSet(ConfigSnapshot source, Dictionary<string, ResolvedBackend> byName, ResolvedBackend @default)
    {
        Source = source;
        _byName = byName;
        Default = @default;
    }

    /// <summary>The snapshot this set was built from — the cache key, compared by reference.</summary>
    internal ConfigSnapshot Source { get; }

    public ResolvedBackend Default { get; }

    public IReadOnlyCollection<ResolvedBackend> All => _byName.Values;

    public string[] Names => _byName.Values.Select(b => b.Name).Order(StringComparer.OrdinalIgnoreCase).ToArray();

    public ResolvedBackend? Find(string name) => _byName.GetValueOrDefault(name);
}

/// <summary>
/// Case-insensitive name → backend lookup. D7 — a view over the store's current
/// <see cref="ConfigSnapshot"/>, rebuilt whenever that snapshot reference changes, so a
/// live config PUT (add/edit/remove a backend, change the default) takes effect for the
/// very next request with no restart.
/// <para>
/// R02: the previous version read <c>Current</c> and <c>Version</c> separately and stored
/// the map and default in separate fields, so a concurrent PUT could label revision N's
/// map as revision N+1 and leave routing permanently stale. Now the whole derived value is
/// cached against the snapshot reference it came from, and a request that needs routing
/// and per-request limits to agree resolves both from one snapshot via
/// <see cref="Resolve(ConfigSnapshot)"/>.
/// </para>
/// </summary>
public sealed class BackendRegistry
{
    private readonly ConfigStore _configStore;
    private readonly object _rebuildLock = new();
    private BackendSet _current;

    public BackendRegistry(ConfigStore configStore)
    {
        _configStore = configStore;
        _current = Build(configStore.Snapshot);
    }

    /// <summary>
    /// The backends of <paramref name="snapshot"/>. Callers that must not straddle a config
    /// swap (the proxy path: routing plus that request's limits/timeouts) take one snapshot
    /// and pass it here.
    /// </summary>
    public BackendSet Resolve(ConfigSnapshot snapshot)
    {
        BackendSet cached = Volatile.Read(ref _current);
        if (ReferenceEquals(cached.Source, snapshot))
        {
            return cached;
        }

        lock (_rebuildLock)
        {
            cached = Volatile.Read(ref _current);
            if (ReferenceEquals(cached.Source, snapshot))
            {
                return cached;
            }

            // Always build (and return) exactly the snapshot the caller asked for — the
            // contract is "the backends for revision X are X's own backends," not "the
            // backends for revision X are whatever's newest by the time the lock is free."
            // A found-and-fixed regression: an earlier version of this method built from
            // `_configStore.Snapshot` (whatever was newest at rebuild time) instead, on the
            // theory that "a caller getting a newer set than it asked for is fine." It
            // isn't, for a caller comparing what it resolved against the snapshot it
            // resolved it *from* (exactly ProxyHandler's shape: one snapshot, then routing
            // and per-request limits from that same snapshot) — a race could hand back a
            // set from a *different* revision than the one being compared against,
            // silently reintroducing a version of R02's original defect one layer up. The
            // concurrency probe below caught this reliably across repeated full-suite runs.
            BackendSet built = Build(snapshot);

            // The `_current` cache is still only ever advanced forward: a slower thread
            // rebuilding an older snapshot must not clobber a newer entry another thread
            // already cached, or the fast path above would start missing again for
            // requests that just want "whatever's current."
            if (built.Source.Version >= cached.Source.Version)
            {
                Volatile.Write(ref _current, built);
            }

            return built;
        }
    }

    /// <summary>The backends of the store's latest snapshot — for callers with no request scope (status, startup banner).</summary>
    public BackendSet Latest => Resolve(_configStore.Snapshot);

    public ResolvedBackend Default => Latest.Default;

    public IReadOnlyCollection<ResolvedBackend> All => Latest.All;

    public string[] Names => Latest.Names;

    public ResolvedBackend? Find(string name) => Latest.Find(name);

    private static BackendSet Build(ConfigSnapshot snapshot)
    {
        VesselConfig config = snapshot.Config;
        var byName = new Dictionary<string, ResolvedBackend>(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, BackendConfig backend) in config.Backends)
        {
            bool isDefault = string.Equals(name, config.DefaultBackend, StringComparison.OrdinalIgnoreCase);
            byName[name] = new ResolvedBackend(
                name, backend.BaseUrl.TrimEnd('/'), backend.Type, isDefault, backend.InjectStreamUsage);
        }

        return new BackendSet(snapshot, byName, byName.Values.Single(b => b.IsDefault));
    }
}
