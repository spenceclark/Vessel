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
}
