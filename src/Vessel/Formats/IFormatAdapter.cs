using System.Text.Json.Nodes;

namespace Vessel.Formats;

/// <summary>Everything an adapter needs, decoded and pre-parsed by the enricher.</summary>
/// <param name="Request">The parsed request JSON, or null when absent/unparseable.</param>
/// <param name="ResponseText">
/// The decoded response body as text (raw SSE/NDJSON stream, or a non-streamed JSON
/// document), or null for error rows where no real backend response exists (D2).
/// </param>
/// <param name="Streamed">Whether the response was a stream (drives SSE/NDJSON reassembly).</param>
public readonly record struct AdapterInput(JsonNode? Request, string? ResponseText, bool Streamed);

/// <summary>
/// The normalized fields an adapter extracts from one captured exchange (D5/D6/D9).
/// Adapters never throw on malformed input — truncated streams are expected input — so a
/// field simply stays null when it can't be found.
/// </summary>
public sealed class AdapterResult
{
    public string? Model { get; set; }

    public long? TokensIn { get; set; }

    public long? TokensOut { get; set; }

    public long? TokensCachedRead { get; set; }

    public long? TokensCachedWrite { get; set; }

    public string? StopReason { get; set; }

    /// <summary>The Vessel-synthesized non-streamed document for a streamed response; null otherwise (D5).</summary>
    public byte[]? ReassembledResponse { get; set; }

    public string? PromptText { get; set; }

    public string? ResponseText { get; set; }

    /// <summary>Set only when the adapter has an exact figure (Ollama); otherwise the enricher computes it.</summary>
    public double? TokPerSec { get; set; }

    /// <summary>Format-level warnings the adapter discovered (stream_incomplete, cold_load).</summary>
    public List<string> Warnings { get; } = [];
}

/// <summary>
/// A format adapter: turns one captured exchange into normalized fields. Runs in the
/// background writer, never on the request path (D1).
/// </summary>
public interface IFormatAdapter
{
    AdapterResult Parse(AdapterInput input);
}
