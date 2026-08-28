using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Vessel.Capture;
using Vessel.Config;
using Vessel.Formats;
using Xunit;

namespace Vessel.Tests;

/// <summary>F11 (adapter backstop) and F5 (estimation only fills missing counts).</summary>
public class FormatEnricherTests
{
    private static CaptureRecord Record(string path, string? requestBody, string? responseBody) =>
        TestCapture.Record(path, requestBody, responseBody);

    private static string[] Warns(EnrichedRecord e) =>
        e.WarningsJson is null ? [] : JsonSerializer.Deserialize<string[]>(e.WarningsJson)!;

    private sealed class ThrowingAdapter : IFormatAdapter
    {
        public AdapterResult Parse(AdapterInput input) => throw new InvalidOperationException("boom");
    }

    // F11: an adapter that throws must not lose the row — it falls back to raw + parse_error,
    // bytes intact.
    [Fact]
    public void AdapterThrows_FallsBackToRaw_BytesIntact()
    {
        var adapters = new Dictionary<string, IFormatAdapter> { [FormatNames.OllamaChat] = new ThrowingAdapter() };
        var enricher = new FormatEnricher(new VesselConfig(), adapters);

        CaptureRecord record = Record(
            "/api/chat",
            """{"model":"m","messages":[{"role":"user","content":"hi"}]}""",
            """{"message":{"content":"x"},"done":true,"eval_count":1,"eval_duration":1000000}""");

        EnrichedRecord enriched = enricher.Enrich(record);

        Assert.Equal(FormatNames.Raw, enriched.Format);
        Assert.Contains(Warnings.ParseError, Warns(enriched));
        Assert.Same(record, enriched.Record); // original wire bytes preserved
        Assert.Null(enriched.Model);
        Assert.Null(enriched.PromptText);
        Assert.Null(enriched.ResponseText);
    }

    private sealed class PartialUsageAdapter : IFormatAdapter
    {
        public AdapterResult Parse(AdapterInput input) => new()
        {
            Model = "m",
            TokensOut = 5, // reported
            TokensIn = null, // missing → should be estimated
            PromptText = "user: some prompt text here", // 27 chars → ceil(27/4)=7
            ResponseText = "hello",
        };
    }

    // F5: estimation fills only the missing count; the reported one is never overwritten,
    // and the row is flagged.
    [Fact]
    public void Estimation_FillsMissingOnly_ReportedPreserved()
    {
        var adapters = new Dictionary<string, IFormatAdapter> { [FormatNames.OpenAiChat] = new PartialUsageAdapter() };
        var enricher = new FormatEnricher(new VesselConfig(), adapters);

        CaptureRecord record = Record("/v1/chat/completions", "{}", "{}");
        EnrichedRecord enriched = enricher.Enrich(record);

        Assert.Equal(5, enriched.TokensOut);   // reported, untouched
        Assert.Equal(7, enriched.TokensIn);    // estimated from prompt length
        Assert.True(enriched.TokensEstimated);
        Assert.Contains(Warnings.TokensEstimated, Warns(enriched));
    }

    // A compressed backend response (OpenAI behind Cloudflare routinely br/gzip-encodes)
    // must not surface as opaque base64 in Vessel's own stored copy — only the wire bytes
    // actually forwarded to the caller (ResponseTeeStream, untouched) stay compressed.
    [Fact]
    public void CompressedResponse_NonStreamed_StoredWireTrue_ButParsed()
    {
        // Corrected, not deleted (D01): an earlier revision made enrichment rewrite
        // response_body to the *decoded* bytes so the detail pane would show JSON, and this
        // test enshrined that. Phase-2 D3 says storage is wire-true; the decode belongs at
        // read time (SqliteReadStore.ToBodyPayload). So the assertion flips — the bytes stay
        // compressed — while the other half of the original intent, that a compressed body
        // is still fully parsed, is asserted here rather than dropped.
        var enricher = new FormatEnricher(new VesselConfig(), FormatEnricher.DefaultAdapters());
        byte[] wire = Gzip(
            """{"id":"c","object":"chat.completion","model":"gpt-4o-mini","choices":[{"index":0,"message":{"role":"assistant","content":"hi"},"finish_reason":"stop"}],"usage":{"prompt_tokens":3,"completion_tokens":1,"total_tokens":4}}"""u8.ToArray());

        CaptureRecord record = Record("/v1/chat/completions", """{"model":"gpt-4o-mini"}""", null) with
        {
            ResponseHeadersJson = """{"Content-Type":["application/json"],"Content-Encoding":["gzip"]}""",
            ResponseBody = wire,
        };

        EnrichedRecord enriched = enricher.Enrich(record);

        Assert.Equal(wire, enriched.Record.ResponseBody);
        Assert.Equal(FormatNames.OpenAiChat, enriched.Format);
        Assert.Equal("gpt-4o-mini", enriched.Model);
        Assert.Equal(1, enriched.TokensOut);
        Assert.Equal("hi", enriched.ResponseText);
    }

    // response_raw backs the UI's "Raw stream" toggle specifically because it's the actual
    // bytes on the wire — decoding it would defeat the point, so it must stay untouched.
    [Fact]
    public void CompressedResponse_Streamed_RawStaysWireAccurate()
    {
        var enricher = new FormatEnricher(new VesselConfig(), FormatEnricher.DefaultAdapters());
        byte[] compressed = Gzip("data: {}\n\n"u8.ToArray());

        CaptureRecord record = TestCapture.Record("/unrecognized", streamed: true) with
        {
            ResponseHeadersJson = """{"Content-Type":["text/event-stream"],"Content-Encoding":["gzip"]}""",
            ResponseRaw = compressed,
        };

        EnrichedRecord enriched = enricher.Enrich(record);

        Assert.Equal(compressed, enriched.Record.ResponseRaw);
    }

    private static byte[] Gzip(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionMode.Compress))
        {
            gzip.Write(data, 0, data.Length);
        }

        return output.ToArray();
    }

    // D02: whole-request duration mixes queueing/prefill/network into a rate that isn't
    // the same quantity as generation throughput, so a non-streamed non-Ollama row has
    // no tok/s signal at all — regardless of duration, short or long.
    [Fact]
    public void NonStreamedResponse_TokPerSec_IsNull()
    {
        var enricher = new FormatEnricher(new VesselConfig(), FormatEnricher.DefaultAdapters());
        CaptureRecord record = Record(
            "/v1/chat/completions",
            """{"model":"m"}""",
            """{"id":"x","object":"chat.completion","choices":[{"index":0,"message":{"role":"assistant","content":"hi"},"finish_reason":"stop"}],"usage":{"prompt_tokens":5,"completion_tokens":40,"total_tokens":45}}""")
            with
        { DurationMs = 2000 };

        EnrichedRecord enriched = enricher.Enrich(record);

        Assert.Null(enriched.TokPerSec);
    }

    [Fact]
    public void NonStreamedResponse_TooShortDuration_TokPerSecIsNull()
    {
        var enricher = new FormatEnricher(new VesselConfig(), FormatEnricher.DefaultAdapters());
        CaptureRecord record = Record(
            "/v1/chat/completions",
            """{"model":"m"}""",
            """{"id":"x","object":"chat.completion","choices":[{"index":0,"message":{"role":"assistant","content":"hi"},"finish_reason":"stop"}],"usage":{"prompt_tokens":5,"completion_tokens":40,"total_tokens":45}}""")
            with
        { DurationMs = 50 };

        EnrichedRecord enriched = enricher.Enrich(record);

        Assert.Null(enriched.TokPerSec);
    }
}
