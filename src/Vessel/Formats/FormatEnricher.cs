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
    private readonly IReadOnlyDictionary<string, string> _backendTypes;
    private readonly int _slowTtftMs;
    private readonly ILogger<FormatEnricher>? _logger;

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
        _slowTtftMs = config.Warnings.SlowTtftMs;
        _logger = logger;
        _backendTypes = config.Backends.ToDictionary(
            kvp => kvp.Key, kvp => kvp.Value.Type, StringComparer.OrdinalIgnoreCase);
    }

    public static Dictionary<string, IFormatAdapter> DefaultAdapters() => new()
    {
        [FormatNames.OpenAiChat] = new OpenAiChatAdapter(),
        [FormatNames.AnthropicMessages] = new AnthropicMessagesAdapter(),
        [FormatNames.OllamaChat] = new OllamaAdapter(generate: false),
        [FormatNames.OllamaGenerate] = new OllamaAdapter(generate: true),
    };

    public EnrichedRecord Enrich(CaptureRecord record)
    {
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

    /// <summary>Ollama's exact figure when present; otherwise wire timing for streamed rows (D6).</summary>
    private static double? TokPerSec(CaptureRecord record, AdapterResult result, long? tokensOut)
    {
        if (result.TokPerSec is double exact)
        {
            return exact;
        }

        if (record.Streamed && tokensOut is long tokens && tokens > 0
            && record.FirstResponseByteMs is double first && record.LastResponseByteMs is double last)
        {
            double spanMs = last - first;
            if (spanMs >= 100)
            {
                return tokens / (spanMs / 1000.0);
            }
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
