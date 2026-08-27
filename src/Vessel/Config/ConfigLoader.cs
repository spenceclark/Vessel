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

    private static void Validate(VesselConfig config, string path)
    {
        if (config.Backends.Count == 0)
        {
            throw new ConfigException($"config '{path}': no backends configured");
        }

        foreach ((string name, BackendConfig backend) in config.Backends)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ConfigException($"config '{path}': backend with empty name");
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
