using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vessel.Config;

public sealed class VesselConfig
{
    public string Listen { get; set; } = "127.0.0.1:4550";

    public string DefaultBackend { get; set; } = "ollama";

    public Dictionary<string, BackendConfig> Backends { get; set; } = new();

    public TimeoutConfig Timeouts { get; set; } = new();

    // Properties this binary doesn't know about (from a newer Vessel version) must
    // survive a load/save round trip.
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class BackendConfig
{
    public string BaseUrl { get; set; } = "";

    /// <summary>Hint for format parsing and UI affordances: openai | anthropic | ollama | auto.</summary>
    public string Type { get; set; } = "auto";

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class TimeoutConfig
{
    /// <summary>
    /// YARP ActivityTimeout: max time with zero bytes moving in either direction.
    /// Sized for LLM traffic — a cold local model can sit in prompt eval for minutes.
    /// </summary>
    public int ActivitySeconds { get; set; } = 1800;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    WriteIndented = true,
    AllowTrailingCommas = true,
    ReadCommentHandling = JsonCommentHandling.Skip)]
[JsonSerializable(typeof(VesselConfig))]
public sealed partial class ConfigJsonContext : JsonSerializerContext;
