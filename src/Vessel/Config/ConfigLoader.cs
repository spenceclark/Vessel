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

    public static void Save(string path, VesselConfig config)
    {
        string json = JsonSerializer.Serialize(config, ConfigJsonContext.Default.VesselConfig);
        File.WriteAllText(path, json + Environment.NewLine);
    }

    public static VesselConfig CreateDefault() => new()
    {
        Listen = "127.0.0.1:4550",
        DefaultBackend = "ollama",
        Backends = new Dictionary<string, BackendConfig>
        {
            ["ollama"] = new() { BaseUrl = "http://localhost:11434", Type = "ollama" },
        },
        Timeouts = new TimeoutConfig(),
    };

    /// <summary>
    /// D7 — public so <c>ConfigStore.Apply</c> (the <c>PUT /vessel/api/config</c> path) can
    /// validate a candidate config with the exact same rules as startup, before persisting
    /// or applying anything.
    /// </summary>
    public static void Validate(VesselConfig config, string path)
    {
        if (config.Backends.Count == 0)
        {
            throw new ConfigException($"config '{path}': no backends configured");
        }

        // Dictionary<string, BackendConfig> keys are case-sensitive, so two names differing
        // only in case (e.g. "ollama" and "Ollama") both survive JSON deserialization as
        // distinct entries — undetected here, they'd silently collide in BackendRegistry's
        // case-insensitive lookup (D7).
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach ((string name, BackendConfig backend) in config.Backends)
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

            if (!Uri.TryCreate(backend.BaseUrl, UriKind.Absolute, out Uri? uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw new ConfigException(
                    $"config '{path}': backend '{name}' baseUrl '{backend.BaseUrl}' is not an absolute http(s) URL");
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

        if (!TryParseListen(config.Listen, out _, out _))
        {
            throw new ConfigException(
                $"config '{path}': listen '{config.Listen}' is not a valid host:port (e.g. \"127.0.0.1:4550\")");
        }

        if (config.Timeouts.ActivitySeconds <= 0)
        {
            throw new ConfigException($"config '{path}': timeouts.activitySeconds must be positive");
        }

        if (config.Retention.MaxRequests <= 0)
        {
            throw new ConfigException($"config '{path}': retention.maxRequests must be positive");
        }

        if (config.Retention.MaxDbSizeMb <= 0)
        {
            throw new ConfigException($"config '{path}': retention.maxDbSizeMb must be positive");
        }

        if (config.Capture.MaxBodyMb <= 0)
        {
            throw new ConfigException($"config '{path}': capture.maxBodyMb must be positive");
        }

        if (config.Warnings.SlowTtftMs < 0)
        {
            throw new ConfigException($"config '{path}': warnings.slowTtftMs must be zero or positive (0 disables)");
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
