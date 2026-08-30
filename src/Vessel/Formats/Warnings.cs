namespace Vessel.Formats;

/// <summary>
/// D7 warning vocabulary — the string codes stored in the <c>warnings</c> column as a
/// JSON array. The UI maps codes to badges later (Phase 3+). One place for the codes so
/// producers and the UI can't drift.
/// </summary>
public static class Warnings
{
    /// <summary>Stop reason means the response was cut short (<c>length</c> / <c>max_tokens</c>).</summary>
    public const string TruncatedResponse = "truncated_response";

    /// <summary>Backend returned a non-2xx status (real HTTP error, not a proxy failure).</summary>
    public const string HttpError = "http_error";

    /// <summary>Vessel could not get a response from the backend (unreachable, timeout, …).</summary>
    public const string ProxyError = "proxy_error";

    /// <summary>The client went away before the exchange completed.</summary>
    public const string ClientDisconnect = "client_disconnect";

    /// <summary>Token counts were estimated (chars/4), not reported by the backend.</summary>
    public const string TokensEstimated = "tokens_estimated";

    /// <summary>A streamed response never reached its terminal marker (cut off / disconnect / cap).</summary>
    public const string StreamIncomplete = "stream_incomplete";

    /// <summary>Detection or an adapter failed; the row fell back to <c>raw</c> with bytes intact.</summary>
    public const string ParseError = "parse_error";

    /// <summary>The capture buffer hit its <c>maxBodyMb</c> cap; the stored body is truncated.</summary>
    public const string BodyTruncated = "body_truncated";

    /// <summary>Ollama <c>load_duration</c> was large — the model was cold-loading, not slow-generating.</summary>
    public const string ColdLoad = "cold_load";

    /// <summary>Time to first token exceeded the configured threshold (not explained by a cold load).</summary>
    public const string SlowTtft = "slow_ttft";

    /// <summary>Vessel added <c>stream_options.include_usage</c> to this request (D11) — the stored
    /// request bytes are the client's originals; this marks why a usage chunk appeared.</summary>
    public const string UsageInjected = "usage_injected";

    /// <summary>
    /// The request declared tools, but the model emitted a matching tool-call-shaped JSON
    /// object in text instead of a structured tool call. Detection only; Vessel never
    /// rewrites the response.
    /// </summary>
    public const string ToolCallInText = "tool_call_in_text";

    /// <summary>
    /// Canonical ordering used when serializing a row's warnings, so the stored array is
    /// deterministic regardless of the order producers discovered the codes.
    /// </summary>
    private static readonly string[] _order =
    [
        TruncatedResponse, HttpError, ProxyError, ClientDisconnect, TokensEstimated,
        StreamIncomplete, ParseError, BodyTruncated, ColdLoad, SlowTtft, UsageInjected,
        ToolCallInText,
    ];

    /// <summary>Deduplicates and orders codes into the canonical sequence for storage.</summary>
    public static IEnumerable<string> InOrder(IEnumerable<string> codes)
    {
        var present = new HashSet<string>(codes, StringComparer.Ordinal);
        foreach (string code in _order)
        {
            if (present.Contains(code))
            {
                yield return code;
            }
        }
    }
}
