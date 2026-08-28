namespace Vessel.Config;

/// <summary>D7 — <c>PUT /vessel/api/config</c> response: whether it applied, and which fields need a restart.</summary>
public sealed record ConfigApplyResult(bool Applied, string[] RestartRequired);

/// <summary>
/// R16 — <c>GET /vessel/api/config</c> response: the config plus whatever restart is
/// still pending against the *actually bound* listener, so reopening the settings panel
/// shows a warning that a PUT response alone (local component state) couldn't survive.
/// </summary>
public sealed record ConfigGetResult(VesselConfig Config, string[] RestartRequired);

/// <summary>
/// R02 — one config revision as a single immutable reference: the config graph and the
/// revision number that labels it, always published and read together. Consumers key
/// their derived caches on the <em>snapshot reference</em> (see
/// <c>BackendRegistry</c>/<c>FormatEnricher</c>), never on a separately-read version,
/// which is what made a torn read possible before.
/// </summary>
public sealed record ConfigSnapshot(VesselConfig Config, int Version);

/// <summary>
/// D7 — the live-apply seam. Owns the current config as an immutable
/// <see cref="ConfigSnapshot"/> published behind a single <c>Volatile</c> reference.
/// <see cref="Apply"/> validates with the same rules as startup, persists to disk, then
/// swaps the snapshot — serialized under a lock, last write wins (single user, single
/// machine). In-flight requests already hold their own resolved <c>RouteDecision</c>/
/// config values from before the swap and are unaffected; only new requests and the next
/// writer batch see the change.
/// <para>
/// R02: config and version were previously two fields read independently. A PUT landing
/// between a consumer's two reads let a map built from revision N be labelled N+1, after
/// which the consumer considered its stale map current until the *next* PUT — routing
/// prompts to the previous destination indefinitely. Publishing both in one reference
/// makes that interleaving unrepresentable.
/// </para>
/// </summary>
public sealed class ConfigStore
{
    private readonly string _path;
    private readonly object _lock = new();
    private ConfigSnapshot _snapshot;

    // R16 — the address Kestrel is *actually* bound to, captured once after the listener
    // comes up (see RecordBoundListen). Comparing a candidate against this — rather than
    // against whatever was last saved — is what makes "still needs a restart" durable
    // across repeated saves: a save can change the persisted `listen` value without the
    // running process ever rebinding, so the saved value is not a reliable stand-in for
    // where the process actually is. `_boundListenLiteral` is the exact `listen` string in
    // effect at that moment (e.g. "127.0.0.1:0" for an ephemeral port): Kestrel resolves
    // "0" to a real port that no candidate string will ever spell out again, so an
    // unmodified `listen` (still literally "...:0") must compare equal to itself, not to
    // the numeric port it happened to land on.
    private (System.Net.IPAddress Address, int Port)? _boundListen;
    private string? _boundListenLiteral;

    public ConfigStore(VesselConfig initial, string path)
    {
        _snapshot = new ConfigSnapshot(initial, 0);
        _path = path;
    }

    /// <summary>The current revision — config and version as one indivisible reference.</summary>
    public ConfigSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public VesselConfig Current => Snapshot.Config;

    /// <summary>Bumped on every successful <see cref="Apply"/>. Prefer <see cref="Snapshot"/> when the config that goes with it matters.</summary>
    public int Version => Snapshot.Version;

    /// <summary>
    /// Called once, after Kestrel has actually bound its listener (the requested port may
    /// have been 0). Everything after this compares against the real bound endpoint, not
    /// the desired one. Never called by tests that construct a bare <see cref="ConfigStore"/>
    /// without starting a host — <see cref="PendingRestart"/> degrades to comparing against
    /// the initially-constructed config in that case.
    /// </summary>
    public void RecordBoundListen(System.Net.IPAddress address, int port)
    {
        lock (_lock)
        {
            _boundListen = (address, port);
            _boundListenLiteral = _snapshot.Config.Listen;
        }
    }

    /// <summary>
    /// Whether the *currently applied* config still differs from what the process is
    /// actually bound to — i.e. a restart is still pending. Recomputed from state, not
    /// cached, so it stays correct across repeated saves and reverts (R16).
    /// </summary>
    public string[] PendingRestart
    {
        get
        {
            lock (_lock)
            {
                return ListenDiffersFromBound(_snapshot.Config.Listen) ? ["listen"] : [];
            }
        }
    }

    /// <summary>
    /// Validates <paramref name="candidate"/> (throws <see cref="ConfigException"/>,
    /// nothing applied or written, on any failure), persists it to <c>vessel.json</c>,
    /// then swaps in a new snapshot. Only <c>listen</c> requires a restart to actually
    /// take effect — still persisted, but flagged in the result against the address the
    /// process is actually bound to (R16), not against whatever was last saved.
    /// </summary>
    public ConfigApplyResult Apply(VesselConfig candidate)
    {
        lock (_lock)
        {
            ConfigLoader.Validate(candidate, _path);

            ConfigSnapshot previous = _snapshot;
            bool listenChanged = ListenDiffersFromBound(candidate.Listen);

            ConfigLoader.Save(_path, candidate);
            Volatile.Write(ref _snapshot, new ConfigSnapshot(candidate, previous.Version + 1));

            return new ConfigApplyResult(true, listenChanged ? ["listen"] : []);
        }
    }

    /// <summary>Must be called under <see cref="_lock"/> — reads <see cref="_boundListen"/>.</summary>
    private bool ListenDiffersFromBound(string candidateListen)
    {
        if (_boundListen is not { } bound)
        {
            // No host has recorded a bound address yet (unit tests constructing ConfigStore
            // directly) — fall back to the only reference point available, the config this
            // store was constructed with.
            return !string.Equals(_snapshot.Config.Listen, candidateListen, StringComparison.OrdinalIgnoreCase);
        }

        if (string.Equals(_boundListenLiteral, candidateListen, StringComparison.OrdinalIgnoreCase))
        {
            // Exactly the listen value already in effect (whatever port an ephemeral "0"
            // happened to resolve to) — nothing to restart into.
            return false;
        }

        if (!ConfigLoader.TryParseListen(candidateListen, out System.Net.IPAddress candidateAddress, out int candidatePort))
        {
            // Unparseable would already have failed Validate for Apply's caller; for
            // PendingRestart's read-only path, treat it as "differs" rather than throw.
            return true;
        }

        return candidatePort != bound.Port || !candidateAddress.Equals(bound.Address);
    }
}
