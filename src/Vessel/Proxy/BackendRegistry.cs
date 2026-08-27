using Vessel.Config;

namespace Vessel.Proxy;

/// <summary>A configured backend with its name resolved and base URL normalized.</summary>
public sealed record ResolvedBackend(string Name, string BaseUrl, string Type, bool IsDefault, bool InjectStreamUsage);

/// <summary>
/// Case-insensitive name → backend lookup. D7 — a view over <see cref="ConfigStore.Current"/>,
/// rebuilt lazily whenever the store's version has advanced since the last build, so a
/// live config PUT (add/edit/remove a backend, change the default) takes effect for the
/// very next request with no restart.
/// </summary>
public sealed class BackendRegistry
{
    private readonly ConfigStore _configStore;
    private readonly object _rebuildLock = new();
    private Dictionary<string, ResolvedBackend> _byName = new(StringComparer.OrdinalIgnoreCase);
    private ResolvedBackend _default = null!;
    private int _builtVersion = -1;

    public BackendRegistry(ConfigStore configStore)
    {
        _configStore = configStore;
        Rebuild();
    }

    public ResolvedBackend Default
    {
        get
        {
            EnsureCurrent();
            return _default;
        }
    }

    public IReadOnlyCollection<ResolvedBackend> All
    {
        get
        {
            EnsureCurrent();
            return _byName.Values;
        }
    }

    public string[] Names
    {
        get
        {
            EnsureCurrent();
            return _byName.Values.Select(b => b.Name).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }

    public ResolvedBackend? Find(string name)
    {
        EnsureCurrent();
        return _byName.GetValueOrDefault(name);
    }

    private void EnsureCurrent()
    {
        if (_builtVersion == _configStore.Version)
        {
            return;
        }

        lock (_rebuildLock)
        {
            if (_builtVersion == _configStore.Version)
            {
                return;
            }

            Rebuild();
        }
    }

    private void Rebuild()
    {
        VesselConfig config = _configStore.Current;
        var byName = new Dictionary<string, ResolvedBackend>(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, BackendConfig backend) in config.Backends)
        {
            bool isDefault = string.Equals(name, config.DefaultBackend, StringComparison.OrdinalIgnoreCase);
            byName[name] = new ResolvedBackend(
                name, backend.BaseUrl.TrimEnd('/'), backend.Type, isDefault, backend.InjectStreamUsage);
        }

        _byName = byName;
        _default = byName.Values.Single(b => b.IsDefault);
        _builtVersion = _configStore.Version;
    }
}
