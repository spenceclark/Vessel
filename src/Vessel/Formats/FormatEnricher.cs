using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vessel.Capture;
using Vessel.Config;

namespace Vessel.Formats;

/// <summary>
/// D1 — the enrichment entry point, called by the background writer per record before
/// insert. Detection + the chosen adapter run inside a backstop: any exception falls the
/// row back to <c>raw</c> + <c>parse_error</c> with its wire bytes intact, so an adapter
/// bug can never lose a row. Adapters themselves are written not to throw on malformed
/// input (truncated streams are expected); the catch is the safety net.
/// </summary>
public sealed class FormatEnricher
{
    private readonly IReadOnlyDictionary<string, IFormatAdapter> _adapters;
    private readonly ILogger<FormatEnricher>? _logger;
    private readonly ConfigStore? _configStore;
    private IReadOnlyDictionary<string, string> _backendTypes = new Dictionary<string, string>();
    private int _slowTtftMs;

    /// <summary>R05 — decoded output shares the capture cap (D01); derived with the rest of the config-dependent state.</summary>
    private long _maxDecodedBytes;

    // R02: keyed by the snapshot reference the derived state was built from, not by a
    // separately-read version number (which a concurrent Apply could mismatch against the
    // config actually read). Only ever touched on the single writer thread.
    private ConfigSnapshot? _builtFrom;

    /// <summary>Static snapshot — used by tests and anywhere a live config isn't wired up. Never re-reads <paramref name="config"/>.</summary>
    public FormatEnricher(VesselConfig config, ILogger<FormatEnricher>? logger = null)
        : this(config, DefaultAdapters(), logger)
    {
    }

    public FormatEnricher(
        VesselConfig config,
        IReadOnlyDictionary<string, IFormatAdapter> adapters,
        ILogger<FormatEnricher>? logger = null)
    {
        _adapters = adapters;
        _logger = logger;
        // No store to track: a fixed revision that never advances (_configStore stays null,
        // so EnsureCurrent is a no-op).
        RebuildFrom(new ConfigSnapshot(config, 0));
    }

    /// <summary>D7 — live: re-derives <see cref="_backendTypes"/>/<see cref="_slowTtftMs"/> whenever <paramref name="configStore"/> publishes a new snapshot.</summary>
    public FormatEnricher(ConfigStore configStore, ILogger<FormatEnricher>? logger = null)
        : this(configStore, DefaultAdapters(), logger)
    {
    }

    public FormatEnricher(
        ConfigStore configStore,
        IReadOnlyDictionary<string, IFormatAdapter> adapters,
        ILogger<FormatEnricher>? logger = null)
    {
        _adapters = adapters;
        _logger = logger;
        _configStore = configStore;
        RebuildFrom(configStore.Snapshot);
    }

    private void RebuildFrom(ConfigSnapshot snapshot)
    {
        VesselConfig config = snapshot.Config;
        _slowTtftMs = config.Warnings.SlowTtftMs;
        _maxDecodedBytes = CaptureBudget.MaxDecodedBytes(config);
        _backendTypes = config.Backends.ToDictionary(
            kvp => kvp.Key, kvp => kvp.Value.Type, StringComparer.OrdinalIgnoreCase);
        _builtFrom = snapshot;
    }

    private void EnsureCurrent()
    {
        if (_configStore is null)
        {
            return;
        }

        // One read; the config that gets derived is the same one the cache key records.
        ConfigSnapshot snapshot = _configStore.Snapshot;
        if (ReferenceEquals(_builtFrom, snapshot))
        {
            return;
        }

        RebuildFrom(snapshot);
    }

    public static Dictionary<string, IFormatAdapter> DefaultAdapters() => new()
    {
        [FormatNames.OpenAiChat] = new OpenAiChatAdapter(),
        [FormatNames.OpenAiResponses] = new OpenAiResponsesAdapter(),
        [FormatNames.AnthropicMessages] = new AnthropicMessagesAdapter(),
        [FormatNames.OllamaChat] = new OllamaAdapter(generate: false),
        [FormatNames.OllamaGenerate] = new OllamaAdapter(generate: true),
    };

    public EnrichedRecord Enrich(CaptureRecord record)
    {
        EnsureCurrent();
        try
        {
            return EnrichCore(record);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "enrichment failed for {Path}; row falls back to raw", record.Path);
            return Raw(record, parseError: true);
        }
    }

    private EnrichedRecord EnrichCore(CaptureRecord record)
    {
        // D01/phase-2 D3: decoding is a *scratch* step for parsing and FTS only. The record's
        // bodies stay exactly as they came off the wire — an earlier revision wrote the
        // decoded bytes back here so the detail pane would show JSON, which silently traded
        // wire fidelity for display convenience; the detail endpoint now decodes for display
        // itself (under the same R05 budget), so storage doesn't have to lie.
        BodyDecoder.Result request = BodyDecoder.Decode(
            record.RequestBody, HeaderValue(record.RequestHeadersJson, "Content-Encoding"), _maxDecodedBytes);
        byte[]? responseWire = record.Streamed ? record.ResponseRaw : record.ResponseBody;
        BodyDecoder.Result response = BodyDecoder.Decode(
            responseWire, HeaderValue(record.ResponseHeadersJson, "Content-Encoding"), _maxDecodedBytes);

        if (request.Status == BodyDecoder.DecodeStatus.Failed || response.Status == BodyDecoder.DecodeStatus.Failed)
        {
            return Raw(record, parseError: true);
        }

        // R05 — a decode that hit the budget yields a prefix, which is the same situation as
        // a capture that hit maxBodyMb: phase-2 D3/D4 say parse as far as it goes and flag
        // the row, and R08 says don't discard content that genuinely arrived. So it flows on
        // (adapters are truncation-tolerant by design) carrying `body_truncated`. A prefix of
        // a non-streamed JSON document simply fails to parse, so nothing wrong is extracted.
        bool decodeTruncated = request.Status == BodyDecoder.DecodeStatus.TruncatedDecode
            || response.Status == BodyDecoder.DecodeStatus.TruncatedDecode;

        JsonNode? requestNode = request.Bytes is null ? null : JsonUtil.Parse(Utf8(request.Bytes));

        // R08 — "is there a real upstream response to read", not "did the request succeed".
        // Treating every proxy error as no-response threw away genuinely received content:
        // a disconnect *after* streamed tokens arrived lost its partial reassembly,
        // response_text and FTS row, contradicting phase-2 D4's partial-stream behaviour.
        // A pre-response failure (unreachable, timeout, unknown backend) still has nothing
        // to parse — there the buffer holds Vessel's own error body, flagged at the source.
        bool hasRealResponse = !record.ResponseAuthoredByVessel && responseWire is { Length: > 0 };
        string? responseText = hasRealResponse && response.Bytes is not null ? Utf8(response.Bytes) : null;

        string format = FormatDetector.Detect(
            record.Path, requestNode, responseText, _backendTypes.GetValueOrDefault(record.Backend));

        if (format == FormatNames.Raw)
        {
            return Raw(record, parseError: false, decodeTruncated);
        }

        AdapterResult result = _adapters[format].Parse(new AdapterInput(requestNode, responseText, record.Streamed));

        long? tokensIn = result.TokensIn;
        long? tokensOut = result.TokensOut;
        bool estimated = false;

        // Estimate only for a normal exchange: a real backend response that isn't an HTTP
        // error. A 4xx/5xx didn't produce a completion, so estimating its tokens is noise.
        if (hasRealResponse && record.StatusCode is null or < 400)
        {
            if (tokensOut is null && TokenEstimator.Estimate(result.ResponseText) is long outEstimate)
            {
                tokensOut = outEstimate;
                estimated = true;
            }

            if (tokensIn is null && TokenEstimator.Estimate(result.PromptText) is long inEstimate)
            {
                tokensIn = inEstimate;
                estimated = true;
            }
        }

        double? tokPerSec = TokPerSec(record, result, tokensOut);
        JsonNode? responseNode = result.ReassembledResponse is not null
            ? JsonUtil.Parse(Utf8(result.ReassembledResponse))
            : JsonUtil.Parse(responseText);
        if (ToolCallInTextDetector.IsDetected(requestNode, responseNode, result.ResponseText))
        {
            result.Warnings.Add(Warnings.ToolCallInText);
        }

        string? warningsJson = SerializeWarnings(
            BuildWarnings(record, result.Warnings, result.StopReason, estimated, decodeTruncated));

        return new EnrichedRecord(
            record, format, result.Model, tokPerSec,
            tokensIn, tokensOut, result.TokensCachedRead, result.TokensCachedWrite,
            estimated, result.StopReason, warningsJson,
            result.ReassembledResponse, result.PromptText, result.ResponseText);
    }

    /// <summary>
    /// Ollama's exact figure when present; otherwise wire timing for streamed rows (D6).
    /// A non-streamed non-Ollama row has no generation-rate signal at all: the backend
    /// computes the whole thing before sending any of it, so the only span available is
    /// total request duration — which mixes in queueing, prefill, and network time and
    /// is not the same quantity as tokens/sec (D02). It stays null rather than reporting
    /// a different metric under the same name.
    /// </summary>
    private static double? TokPerSec(CaptureRecord record, AdapterResult result, long? tokensOut)
    {
        if (result.TokPerSec is double exact)
        {
            return exact;
        }

        if (!record.Streamed)
        {
            return null;
        }

        if (tokensOut is not long tokens || tokens <= 0)
        {
            return null;
        }

        if (record.FirstResponseByteMs is double first && record.LastResponseByteMs is double last)
        {
            double spanMs = last - first;
            if (spanMs >= 100)
            {
                return tokens / (spanMs / 1000.0);
            }
        }

        return null;
    }

    private EnrichedRecord Raw(CaptureRecord record, bool parseError, bool decodeTruncated = false)
    {
        var warnings = BuildWarnings(record, [], stopReason: null, estimated: false, decodeTruncated);
        if (parseError)
        {
            warnings.Add(Warnings.ParseError);
        }

        return new EnrichedRecord(
            record, FormatNames.Raw, Model: null, TokPerSec: null,
            TokensIn: null, TokensOut: null, TokensCachedRead: null, TokensCachedWrite: null,
            TokensEstimated: false, StopReason: null, SerializeWarnings(warnings),
            ReassembledResponse: null, PromptText: null, ResponseText: null);
    }

    private List<string> BuildWarnings(
        CaptureRecord record, IReadOnlyList<string> adapterWarnings, string? stopReason, bool estimated,
        bool decodeTruncated = false)
    {
        var warnings = new List<string>(adapterWarnings);

        if (stopReason is "length" or "max_tokens")
        {
            warnings.Add(Warnings.TruncatedResponse);
        }

        if (estimated)
        {
            warnings.Add(Warnings.TokensEstimated);
        }

        // http_error and proxy_error are mutually exclusive: a proxy failure is not a
        // backend HTTP status (see phase-2.md D7 clarification).
        if (record.Error == Api.VesselErrors.ClientDisconnect)
        {
            warnings.Add(Warnings.ClientDisconnect);
        }
        else if (record.Error is not null)
        {
            warnings.Add(Warnings.ProxyError);
        }
        else if (record.StatusCode >= 400)
        {
            warnings.Add(Warnings.HttpError);
        }

        // R05: a decoded body cut off at the budget is "the body was cut off" to the user,
        // exactly like a wire capture that hit maxBodyMb — same warning, one concept.
        if (record.Truncated || decodeTruncated)
        {
            warnings.Add(Warnings.BodyTruncated);
        }

        if (record.UsageInjected)
        {
            warnings.Add(Warnings.UsageInjected);
        }

        if (_slowTtftMs > 0 && record.TtftMs > _slowTtftMs && !warnings.Contains(Warnings.ColdLoad))
        {
            warnings.Add(Warnings.SlowTtft);
        }

        return warnings;
    }

    private static string? SerializeWarnings(IEnumerable<string> warnings)
    {
        string[] ordered = Warnings.InOrder(warnings).ToArray();
        return ordered.Length == 0
            ? null
            : JsonSerializer.Serialize(ordered, CaptureJsonContext.Default.StringArray);
    }

    private static string Utf8(byte[] bytes) => Encoding.UTF8.GetString(bytes);

    private static string? HeaderValue(string? headersJson, string name)
    {
        if (JsonUtil.Object(JsonUtil.Parse(headersJson)) is not JsonObject headers)
        {
            return null;
        }

        foreach (KeyValuePair<string, JsonNode?> header in headers)
        {
            if (string.Equals(header.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                return JsonUtil.Str(JsonUtil.Array(header.Value)?.FirstOrDefault());
            }
        }

        return null;
    }
}
