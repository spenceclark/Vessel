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
    private int _builtVersion = -1;

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
        RebuildFrom(config);
    }

    /// <summary>D7 — live: re-derives <see cref="_backendTypes"/>/<see cref="_slowTtftMs"/> whenever <paramref name="configStore"/>'s version advances.</summary>
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
        RebuildFrom(configStore.Current);
        _builtVersion = configStore.Version;
    }

    private void RebuildFrom(VesselConfig config)
    {
        _slowTtftMs = config.Warnings.SlowTtftMs;
        _backendTypes = config.Backends.ToDictionary(
            kvp => kvp.Key, kvp => kvp.Value.Type, StringComparer.OrdinalIgnoreCase);
    }

    private void EnsureCurrent()
    {
        if (_configStore is null || _builtVersion == _configStore.Version)
        {
            return;
        }

        RebuildFrom(_configStore.Current);
        _builtVersion = _configStore.Version;
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
        BodyDecoder.Result request = BodyDecoder.Decode(
            record.RequestBody, HeaderValue(record.RequestHeadersJson, "Content-Encoding"));
        byte[]? responseWire = record.Streamed ? record.ResponseRaw : record.ResponseBody;
        BodyDecoder.Result response = BodyDecoder.Decode(
            responseWire, HeaderValue(record.ResponseHeadersJson, "Content-Encoding"));

        // Store the decoded bytes, not the wire-compressed ones, so a compressed backend
        // response (OpenAI/Cloudflare routinely br- or gzip-encodes) reads as JSON instead
        // of tripping SqliteReadStore's UTF-8 check and rendering as opaque base64. This is
        // Vessel's own captured copy only — ResponseTeeStream already forwarded the original
        // compressed bytes to the caller untouched, and response_raw (the streamed "Raw
        // stream" toggle, §5) is deliberately left wire-accurate here.
        if (!record.Streamed && response.Status == BodyDecoder.DecodeStatus.Ok && response.Bytes is not null)
        {
            record = record with { ResponseBody = response.Bytes };
        }

        if (request.Status == BodyDecoder.DecodeStatus.Failed || response.Status == BodyDecoder.DecodeStatus.Failed)
        {
            return Raw(record, parseError: true);
        }

        JsonNode? requestNode = request.Bytes is null ? null : JsonUtil.Parse(Utf8(request.Bytes));

        // Error rows enrich from the request side alone — the "response" is Vessel's own
        // error body or absent, never a real backend response (D2).
        bool hasRealResponse = record.Error is null;
        string? responseText = hasRealResponse && response.Bytes is not null ? Utf8(response.Bytes) : null;

        string format = FormatDetector.Detect(
            record.Path, requestNode, responseText, _backendTypes.GetValueOrDefault(record.Backend));

        if (format == FormatNames.Raw)
        {
            return Raw(record, parseError: false);
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
        string? warningsJson = SerializeWarnings(
            BuildWarnings(record, result.Warnings, result.StopReason, estimated));

        return new EnrichedRecord(
            record, format, result.Model, tokPerSec,
            tokensIn, tokensOut, result.TokensCachedRead, result.TokensCachedWrite,
            estimated, result.StopReason, warningsJson,
            result.ReassembledResponse, result.PromptText, result.ResponseText);
    }

    /// <summary>
    /// Ollama's exact figure when present; otherwise wire timing for streamed rows (D6).
    /// A non-streamed response has no wire timing worth using — the backend computes the
    /// whole thing before sending any of it, so first/last byte land together at the very
    /// end and a first-to-last-byte span would measure ~0, not generation time — so it
    /// falls back to total duration instead. Coarser (it folds in network/queueing time
    /// no streamed figure would), but still a real approximation rather than nothing.
    /// </summary>
    private static double? TokPerSec(CaptureRecord record, AdapterResult result, long? tokensOut)
    {
        if (result.TokPerSec is double exact)
        {
            return exact;
        }

        if (tokensOut is not long tokens || tokens <= 0)
        {
            return null;
        }

        if (record.Streamed)
        {
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

        if (record.DurationMs is double duration && duration >= 100)
        {
            return tokens / (duration / 1000.0);
        }

        return null;
    }

    private EnrichedRecord Raw(CaptureRecord record, bool parseError)
    {
        var warnings = BuildWarnings(record, [], stopReason: null, estimated: false);
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
        CaptureRecord record, IReadOnlyList<string> adapterWarnings, string? stopReason, bool estimated)
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

        if (record.Truncated)
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
