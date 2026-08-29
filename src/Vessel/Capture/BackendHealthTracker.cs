using System.Collections.Concurrent;
using Vessel.Api;
using Vessel.Storage;
using Yarp.ReverseProxy.Forwarder;

namespace Vessel.Capture;

/// <summary>
/// Passive backend reachability inferred from captured proxy outcomes. This deliberately
/// never contacts a backend: only real traffic can change an outcome.
/// </summary>
public sealed class BackendHealthTracker(SqliteReadStore readStore)
{
    public const string Green = "green";
    public const string Red = "red";
    public const string Unknown = "unknown";

    private readonly ConcurrentDictionary<string, BackendHealth> _outcomes =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Seeds the in-memory state from the last persisted capture per backend.</summary>
    public void Seed()
    {
        _outcomes.Clear();
        foreach (BackendHealthSeed outcome in readStore.ReadBackendHealthSeeds())
        {
            _outcomes[outcome.Backend] = ToHealth(outcome.Error, outcome.StartedAt);
        }
    }

    /// <summary>Records an outcome after Vessel has captured a real proxied request.</summary>
    public void Observe(CaptureRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.Backend) || !IsHealthOutcome(record.Error))
        {
            return;
        }

        _outcomes[record.Backend] = ToHealth(record.Error, record.StartedAt);
    }

    public BackendHealth Get(string backend) =>
        _outcomes.TryGetValue(backend, out BackendHealth? health)
            ? health
            : new BackendHealth(Unknown, null);

    public static bool IsUnavailable(string? error) => error is
        VesselErrors.UpstreamUnreachable or
        VesselErrors.UpstreamTimeout or
        nameof(ForwarderError.Request) or
        nameof(ForwarderError.RequestTimedOut);

    public static bool IsHealthOutcome(string? error) => error is null || IsUnavailable(error);

    private static BackendHealth ToHealth(string? error, string startedAt) =>
        IsUnavailable(error)
            ? new BackendHealth(Red, startedAt)
            : new BackendHealth(Green, startedAt);
}

/// <summary>The public status representation of a backend's passively observed state.</summary>
public sealed record BackendHealth(string State, string? LastSeenAt);
