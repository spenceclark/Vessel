namespace Vessel.Config;

/// <summary>D7 — <c>PUT /vessel/api/config</c> response: whether it applied, and which fields need a restart.</summary>
public sealed record ConfigApplyResult(bool Applied, string[] RestartRequired);

/// <summary>
/// D7 — the live-apply seam. Owns the current <see cref="VesselConfig"/> as an immutable
/// snapshot behind <see cref="Current"/>, plus a version counter consumers use to know
/// when to rebuild their own derived state (<c>BackendRegistry</c>'s name lookup,
/// <c>FormatEnricher</c>'s backend-type map). <see cref="Apply"/> validates with the same
/// rules as startup, persists to disk, then swaps the snapshot and bumps the version —
/// serialized under a lock, last write wins (single user, single machine). In-flight
/// requests already hold their own resolved <c>RouteDecision</c>/config values from before
/// the swap and are unaffected; only new requests and the next writer batch see the change.
/// </summary>
public sealed class ConfigStore
{
    private readonly string _path;
    private readonly object _lock = new();
    private VesselConfig _current;
    private int _version;

    public ConfigStore(VesselConfig initial, string path)
    {
        _current = initial;
        _path = path;
    }

    public VesselConfig Current => Volatile.Read(ref _current);

    /// <summary>Bumped on every successful <see cref="Apply"/>; consumers compare against their last-seen value to know when to rebuild.</summary>
    public int Version => Volatile.Read(ref _version);

    /// <summary>
    /// Validates <paramref name="candidate"/> (throws <see cref="ConfigException"/>,
    /// nothing applied or written, on any failure), persists it to <c>vessel.json</c>,
    /// then swaps the snapshot and bumps the version. Only <c>listen</c> requires a
    /// restart to actually take effect — still persisted, but flagged in the result.
    /// </summary>
    public ConfigApplyResult Apply(VesselConfig candidate)
    {
        lock (_lock)
        {
            ConfigLoader.Validate(candidate, _path);

            bool listenChanged = !string.Equals(Current.Listen, candidate.Listen, StringComparison.OrdinalIgnoreCase);

            ConfigLoader.Save(_path, candidate);
            Volatile.Write(ref _current, candidate);
            Interlocked.Increment(ref _version);

            return new ConfigApplyResult(true, listenChanged ? ["listen"] : []);
        }
    }
}
