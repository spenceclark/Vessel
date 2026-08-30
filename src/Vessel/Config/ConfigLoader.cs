using System.Text.Json;

namespace Vessel.Config;

/// <summary>Raised for any config problem the user must fix; the message is printed and Vessel exits non-zero.</summary>
public sealed class ConfigException(string message) : Exception(message);

public static class ConfigLoader
{
    /// <summary>
    /// Loads <paramref name="path"/>, creating it with the default config if absent.
    /// Malformed or invalid config throws <see cref="ConfigException"/> — never silently
    /// falls back to defaults over a typo.
    /// </summary>
    public static (VesselConfig Config, bool Created) LoadOrCreate(string path)
    {
        if (!File.Exists(path))
        {
            var config = CreateDefault();
            Save(path, config);
            return (config, true);
        }

        VesselConfig? loaded;
        try
        {
            using FileStream stream = File.OpenRead(path);
            loaded = JsonSerializer.Deserialize(stream, ConfigJsonContext.Default.VesselConfig);
        }
        catch (JsonException ex)
        {
            throw new ConfigException($"config file '{path}' is not valid JSON: {ex.Message}");
        }
        catch (IOException ex)
        {
            throw new ConfigException($"cannot read config file '{path}': {ex.Message}");
        }

        if (loaded is null)
        {
            throw new ConfigException($"config file '{path}' is empty");
        }

        Validate(loaded, path);
        return (loaded, false);
    }

    /// <summary>
    /// R21 — writes to a temp file in <paramref name="path"/>'s own directory, then
    /// replaces the destination only after that write fully succeeds. A save that fails
    /// partway (disk full, process killed, permission revoked) never truncates or
    /// partially overwrites the last valid config: the destination is either the old
    /// content or the new content, never neither. The temp file is cleaned up on any
    /// failure path.
    /// </summary>
    public static void Save(string path, VesselConfig config)
    {
        string json = JsonSerializer.Serialize(config, ConfigJsonContext.Default.VesselConfig);
        string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        string tempPath = Path.Combine(directory, $".{Path.GetFileName(path)}.tmp-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(tempPath, json + Environment.NewLine);

            if (File.Exists(path))
            {
                // Same volume (same directory) → atomic rename at the filesystem level;
                // the destination is never seen half-written.
                File.Replace(tempPath, path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tempPath, path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDeleteTempFile(tempPath);
            throw new ConfigException($"failed to save config to '{path}': {ex.Message}");
        }
    }

    private static void TryDeleteTempFile(string tempPath)
    {
        try
        {
            File.Delete(tempPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup — the save has already failed and been reported;
            // a stray temp file is cosmetic, not a data-loss risk.
        }
    }

    public static VesselConfig CreateDefault() => new()
    {
        Listen = IsRunningInContainer ? "0.0.0.0:4550" : "127.0.0.1:4550",
        DefaultBackend = "ollama",
        Backends = new Dictionary<string, BackendConfig>
        {
            ["ollama"] = new()
            {
                BaseUrl = IsRunningInContainer ? "http://host.docker.internal:11434" : "http://localhost:11434",
                Type = "ollama",
            },
        },
        Timeouts = new TimeoutConfig(),
    };

    public static bool IsRunningInContainer =>
        string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "1", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// D7 — public so <c>ConfigStore.Apply</c> (the <c>PUT /vessel/api/config</c> path) can
    /// validate a candidate config with the exact same rules as startup, before persisting
    /// or applying anything.
    /// </summary>
    public static void Validate(VesselConfig config, string path)
    {
        // R15 — non-nullable C# declarations don't stop `null` JSON literals from landing
        // here (`PUT {"backends":null}` deserializes fine); every object-shaped section is
        // checked for null before any of its members are read, so a null section becomes a
        // ConfigException, never a NullReferenceException that the endpoint can't turn into
        // a 400.
        if (config.Backends is null || config.Backends.Count == 0)
        {
            throw new ConfigException($"config '{path}': no backends configured");
        }

        // Dictionary<string, BackendConfig> keys are case-sensitive, so two names differing
        // only in case (e.g. "ollama" and "Ollama") both survive JSON deserialization as
        // distinct entries — undetected here, they'd silently collide in BackendRegistry's
        // case-insensitive lookup (D7).
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach ((string name, BackendConfig? backend) in config.Backends)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ConfigException($"config '{path}': backend with empty name");
            }

            if (!seenNames.Add(name))
            {
                throw new ConfigException(
                    $"config '{path}': duplicate backend name '{name}' (backend names are case-insensitive)");
            }

            if (backend is null)
            {
                throw new ConfigException($"config '{path}': backend '{name}' is null");
            }

            if (backend.AuthEnv is not null && string.IsNullOrWhiteSpace(backend.AuthEnv))
            {
                throw new ConfigException($"config '{path}': backend '{name}' authEnv must be a non-empty environment variable name");
            }

            if (!Uri.TryCreate(backend.BaseUrl, UriKind.Absolute, out Uri? uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw new ConfigException(
                    $"config '{path}': backend '{name}' baseUrl '{backend.BaseUrl}' is not an absolute http(s) URL");
            }

            // #5 — plaintext http:// leaks API keys and prompts on the wire; only allow it
            // for hosts that never leave the local machine or LAN (loopback, RFC1918,
            // .local/.internal). Public hosts (and real APIs are https-only anyway) must use
            // https. This runs both at startup and on PUT /vessel/api/config (D7).
            if (uri.Scheme == Uri.UriSchemeHttp && !IsLoopbackOrPrivateHost(uri.Host))
            {
                throw new ConfigException(
                    $"config '{path}': backend '{name}' baseUrl '{backend.BaseUrl}' uses http:// for a " +
                    "publicly routable host; use https://");
            }
        }

        if (string.IsNullOrWhiteSpace(config.DefaultBackend))
        {
            throw new ConfigException($"config '{path}': defaultBackend is not set");
        }

        if (!config.Backends.Keys.Any(k => string.Equals(k, config.DefaultBackend, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ConfigException(
                $"config '{path}': defaultBackend '{config.DefaultBackend}' is not a configured backend " +
                $"(configured: {string.Join(", ", config.Backends.Keys)})");
        }

        if (config.Listen is null || !TryParseListen(config.Listen, out _, out _))
        {
            throw new ConfigException(
                $"config '{path}': listen '{config.Listen}' is not a valid host:port (e.g. \"127.0.0.1:4550\")");
        }

        if (config.Timeouts is null)
        {
            throw new ConfigException($"config '{path}': timeouts is null");
        }

        if (config.Timeouts.ActivitySeconds <= 0)
        {
            throw new ConfigException($"config '{path}': timeouts.activitySeconds must be positive");
        }

        if (config.Retention is null)
        {
            throw new ConfigException($"config '{path}': retention is null");
        }

        if (config.Retention.MaxRequests <= 0)
        {
            throw new ConfigException($"config '{path}': retention.maxRequests must be positive");
        }

        if (config.Retention.MaxDbSizeMb <= 0)
        {
            throw new ConfigException($"config '{path}': retention.maxDbSizeMb must be positive");
        }

        if (config.Capture is null)
        {
            throw new ConfigException($"config '{path}': capture is null");
        }

        if (config.Capture.MaxBodyMb <= 0)
        {
            throw new ConfigException($"config '{path}': capture.maxBodyMb must be positive");
        }

        if (config.Warnings is null)
        {
            throw new ConfigException($"config '{path}': warnings is null");
        }

        if (config.Warnings.SlowTtftMs < 0)
        {
            throw new ConfigException($"config '{path}': warnings.slowTtftMs must be zero or positive (0 disables)");
        }

        if (config.Mcp is null)
        {
            throw new ConfigException($"config '{path}': mcp is null");
        }
    }

    /// <summary>
    /// Non-fatal configuration warnings surfaced at startup (D11): <c>injectStreamUsage</c>
    /// is only meaningful on <c>type: openai</c> backends. Returned rather than thrown —
    /// these never stop Vessel from starting.
    /// </summary>
    public static IReadOnlyList<string> CollectWarnings(VesselConfig config)
    {
        var warnings = new List<string>();
        foreach ((string name, BackendConfig backend) in config.Backends)
        {
            if (backend.InjectStreamUsage && !string.Equals(backend.Type, "openai", StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add(
                    $"backend '{name}': injectStreamUsage is only meaningful on type 'openai' " +
                    $"(this backend is '{backend.Type}'); it will be ignored");
            }
        }

        return warnings;
    }

    /// <summary>
    /// #5 — true for hosts that can only ever be reached from this machine or its LAN:
    /// loopback (<c>localhost</c>, 127.0.0.0/8, ::1), RFC1918 private IPv4 ranges
    /// (10/8, 172.16/12, 192.168/16), and the <c>.local</c>/<c>.internal</c> hostname
    /// suffixes used by mDNS and container/LAN setups. Everything else — including any
    /// other public DNS name — is treated as publicly routable.
    /// </summary>
    private static bool IsLoopbackOrPrivateHost(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (System.Net.IPAddress.TryParse(host, out System.Net.IPAddress? address))
        {
            if (System.Net.IPAddress.IsLoopback(address))
            {
                return true;
            }

            if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                byte[] bytes = address.GetAddressBytes();
                bool is10 = bytes[0] == 10;
                bool is172_16 = bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31;
                bool is192_168 = bytes[0] == 192 && bytes[1] == 168;
                return is10 || is172_16 || is192_168;
            }
        }

        return false;
    }

    public static bool TryParseListen(string listen, out System.Net.IPAddress address, out int port)
    {
        address = System.Net.IPAddress.Loopback;
        port = 0;

        int colon = listen.LastIndexOf(':');
        if (colon <= 0 || colon == listen.Length - 1)
        {
            return false;
        }

        string host = listen[..colon];
        if (!int.TryParse(listen[(colon + 1)..], out port) || port < 0 || port > 65535)
        {
            return false;
        }

        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            address = System.Net.IPAddress.Loopback;
            return true;
        }

        return System.Net.IPAddress.TryParse(host, out address!);
    }
}
