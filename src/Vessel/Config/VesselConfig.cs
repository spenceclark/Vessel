using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vessel.Config;

public sealed class VesselConfig
{
    public string Listen { get; set; } = "127.0.0.1:4550";

    public string DefaultBackend { get; set; } = "ollama";

    public Dictionary<string, BackendConfig> Backends { get; set; } = new();

    public TimeoutConfig Timeouts { get; set; } = new();

    public RetentionConfig Retention { get; set; } = new();

    public CaptureConfig Capture { get; set; } = new();

    public WarningsConfig Warnings { get; set; } = new();

    /// <summary>Whether the read-only MCP endpoint is available on this running host.</summary>
    public McpConfig Mcp { get; set; } = new();

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

    /// <summary>
    /// D11 — for OpenAI-format backends, add <c>stream_options.include_usage</c> to streamed
    /// requests so exact token counts are reported. Off by default; the one request-path
    /// mutation, and only ever on <c>type: openai</c> backends.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool InjectStreamUsage { get; set; }

    /// <summary>
    /// Optional name of the process environment variable that holds this backend's replay
    /// credential. The credential itself is never persisted by Vessel.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AuthEnv { get; set; }

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

public sealed class RetentionConfig
{
    /// <summary>Delete oldest rows beyond this count, enforced after each writer batch.</summary>
    public int MaxRequests { get; set; } = 10_000;

    /// <summary>Delete oldest rows until the database file is under this size.</summary>
    public int MaxDbSizeMb { get; set; } = 500;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class CaptureConfig
{
    /// <summary>
    /// Per-body capture buffer cap. Beyond it the stored copy is truncated and flagged;
    /// the proxied traffic itself is never truncated.
    /// </summary>
    public int MaxBodyMb { get; set; } = 32;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class WarningsConfig
{
    /// <summary>
    /// D7 — a streamed response whose TTFT exceeds this (ms) gets the <c>slow_ttft</c>
    /// warning, unless a cold model load already explains it. <c>0</c> disables the check.
    /// </summary>
    public int SlowTtftMs { get; set; } = 5000;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Read-only Model Context Protocol endpoint settings.</summary>
public sealed class McpConfig
{
    /// <summary>Default-on kill switch, applied live by <see cref="ConfigStore"/>.</summary>
    public bool Enabled { get; set; } = true;

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
