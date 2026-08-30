using System.Net.Sockets;
using Vessel.Config;

namespace Vessel.Proxy;

/// <summary>
/// Issue #11 — first-run setup state. The default backend stays Ollama, so a machine
/// without Ollama gets a dead default and a <c>502 upstream_unreachable</c> the first time
/// a client points at Vessel. Passive health (<see cref="Capture.BackendHealthTracker"/>)
/// can't answer that before any traffic exists, so a first run — and only a first run —
/// asks the question once, and the UI leads with the backend picker when the answer is no.
/// </summary>
public sealed class FirstRunState(bool isFirstRun)
{
    private const int Unprobed = 0;
    private const int Reachable = 1;
    private const int Unreachable = 2;

    private int _defaultBackendProbe = Unprobed;

    /// <summary>True when this process created <c>vessel.json</c> rather than loading one.</summary>
    public bool IsFirstRun { get; } = isFirstRun;

    /// <summary>The one-shot probe's answer, or null when no probe ran (any run but the first).</summary>
    public bool? DefaultBackendReachable => Volatile.Read(ref _defaultBackendProbe) switch
    {
        Reachable => true,
        Unreachable => false,
        _ => null,
    };

    public void RecordProbe(bool reachable) =>
        Volatile.Write(ref _defaultBackendProbe, reachable ? Reachable : Unreachable);
}

/// <summary>
/// A single TCP connect to a backend's host and port. Deliberately not an HTTP request: it
/// sends zero bytes, so it can't be mistaken for API usage, can't cost a token, and needs
/// no credential. It answers exactly one question — is anything listening there — which is
/// all the first-run signpost needs.
/// </summary>
public static class BackendProbe
{
    /// <summary>Loopback refuses instantly; the cap only bounds a filtered or slow-resolving host.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Only hosts that can't be a paid remote API are ever probed (ui-spec.md §9.1: active
    /// probing of live APIs stays rejected — this carve-out is the one-shot local check
    /// that section already anticipated). A freshly created config is always the Ollama
    /// default, so this holds today by construction; the guard is what keeps it true if the
    /// generated default ever changes.
    /// </summary>
    public static bool IsProbeable(string baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && ConfigLoader.IsLoopbackOrPrivateHost(uri.Host);

    public static async Task<bool> IsReachableAsync(string baseUrl, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        try
        {
            using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(uri.Host, uri.Port, cts.Token);
            return socket.Connected;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ArgumentException)
        {
            // Refused, filtered, unresolvable, or slower than the cap — all "not there yet"
            // as far as the first-run signpost is concerned.
            return false;
        }
    }
}

/// <summary>
/// Runs the first-run probe once at startup. No-ops on every later run, so this is never a
/// background health check: <see cref="Capture.BackendHealthTracker"/> remains the only
/// source of the health dots, and it stays passive.
/// </summary>
public sealed class FirstRunProbeService(FirstRunState state, BackendRegistry registry) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!state.IsFirstRun)
        {
            return;
        }

        ResolvedBackend backend = registry.Default;
        if (!BackendProbe.IsProbeable(backend.BaseUrl))
        {
            return;
        }

        state.RecordProbe(
            await BackendProbe.IsReachableAsync(backend.BaseUrl, BackendProbe.DefaultTimeout, cancellationToken));
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
