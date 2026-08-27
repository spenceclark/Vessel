using Vessel.Config;

namespace Vessel.Proxy;

/// <summary>A configured backend with its name resolved and base URL normalized.</summary>
public sealed record ResolvedBackend(string Name, string BaseUrl, string Type, bool IsDefault);

/// <summary>Case-insensitive name → backend lookup, built once from config at startup.</summary>
public sealed class BackendRegistry
{
    private readonly Dictionary<string, ResolvedBackend> _byName;

    public BackendRegistry(VesselConfig config)
    {
        _byName = new Dictionary<string, ResolvedBackend>(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, BackendConfig backend) in config.Backends)
        {
            bool isDefault = string.Equals(name, config.DefaultBackend, StringComparison.OrdinalIgnoreCase);
            _byName[name] = new ResolvedBackend(name, backend.BaseUrl.TrimEnd('/'), backend.Type, isDefault);
        }

        Default = _byName.Values.Single(b => b.IsDefault);
    }

    public ResolvedBackend Default { get; }

    public IReadOnlyCollection<ResolvedBackend> All => _byName.Values;

    public string[] Names => _byName.Values.Select(b => b.Name).Order(StringComparer.OrdinalIgnoreCase).ToArray();

    public ResolvedBackend? Find(string name) => _byName.GetValueOrDefault(name);
}
