using System.Text.Json;
using System.Text.Json.Nodes;
using Vessel.Capture;
using Vessel.Config;
using Vessel.Formats;
using Xunit;

namespace Vessel.Tests;

/// <summary>
/// F1 — the golden suite: every fixture case under <c>Fixtures/</c> is fed through
/// <see cref="FormatEnricher"/> and matched exactly against its <c>expected.json</c>,
/// including the synthesized <c>response_body</c> for streamed cases. This is the
/// highest-value test surface in the project; fixtures are wire-true captures (or, where a
/// live backend wasn't available at authoring time, hand-authored to the documented wire
/// shape — see phase-2.md D12), and malformed/truncated cases are cut by hand from them.
/// </summary>
public class AdapterGoldenTests
{
    private static readonly string _fixturesRoot = Path.Combine(AppContext.BaseDirectory, "Fixtures");

    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (string dir in Directory.EnumerateDirectories(_fixturesRoot, "*", SearchOption.AllDirectories))
        {
            if (File.Exists(Path.Combine(dir, "expected.json")))
            {
                data.Add(Path.GetRelativePath(_fixturesRoot, dir).Replace('\\', '/'));
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Golden(string caseDir)
    {
        string dir = Path.Combine(_fixturesRoot, caseDir);
        using JsonDocument meta = GoldenJson.ReadDocument(Path.Combine(dir, "meta.json"));
        using JsonDocument expected = GoldenJson.ReadDocument(Path.Combine(dir, "expected.json"));
        JsonElement m = meta.RootElement;
        JsonElement e = expected.RootElement;

        byte[]? request = ReadIfExists(Path.Combine(dir, "request.json"));
        byte[]? response = ReadIfExists(Path.Combine(dir, "response.raw"));

        string? responseContentType = Str(m, "responseContentType");
        bool streamed = responseContentType is "text/event-stream" or "application/x-ndjson";

        var record = new CaptureRecord(
            StartedAt: "2026-08-27T00:00:00.0000000Z",
            Seq: 0,
            SessionId: 1,
            Backend: Str(m, "backend") ?? "test",
            TagsJson: null,
            Method: "POST",
            Path: Str(m, "path") ?? "/",
            Format: "raw",
            StatusCode: Int(m, "status") is long s ? (int)s : 200,
            Error: Str(m, "error"),
            Streamed: streamed,
            DurationMs: 1000,
            TtftMs: Double(m, "ttftMs"),
            VesselOverheadMs: 0.1,
            FirstResponseByteMs: Double(m, "firstByteMs"),
            LastResponseByteMs: Double(m, "lastByteMs"),
            RequestHeadersJson: "{\"Content-Type\":[\"application/json\"]}",
            ResponseHeadersJson: ResponseHeaders(responseContentType, Str(m, "responseContentEncoding")),
            RequestBody: request,
            ResponseBody: streamed ? null : response,
            ResponseRaw: streamed ? response : null,
            Truncated: Bool(m, "truncated"),
            UsageInjected: Bool(m, "usageInjected"));

        var config = new VesselConfig
        {
            Backends = Str(m, "backendType") is string type
                ? new Dictionary<string, BackendConfig> { [record.Backend] = new() { BaseUrl = "http://x", Type = type } }
                : new(),
        };

        EnrichedRecord enriched = new FormatEnricher(config).Enrich(record);

        Assert.Equal(Str(e, "format"), enriched.Format);
        Assert.Equal(Str(e, "model"), enriched.Model);
        Assert.Equal(Int(e, "tokensIn"), enriched.TokensIn);
        Assert.Equal(Int(e, "tokensOut"), enriched.TokensOut);
        Assert.Equal(Int(e, "tokensCachedRead"), enriched.TokensCachedRead);
        Assert.Equal(Int(e, "tokensCachedWrite"), enriched.TokensCachedWrite);
        Assert.Equal(Bool(e, "tokensEstimated"), enriched.TokensEstimated);
        Assert.Equal(Str(e, "stopReason"), enriched.StopReason);
        Assert.Equal(Str(e, "promptText"), enriched.PromptText);
        Assert.Equal(Str(e, "responseText"), enriched.ResponseText);

        if (Double(e, "tokPerSec") is double expectedTps)
        {
            Assert.NotNull(enriched.TokPerSec);
            Assert.InRange(enriched.TokPerSec!.Value, expectedTps - 0.5, expectedTps + 0.5);
        }
        else
        {
            Assert.Null(enriched.TokPerSec);
        }

        Assert.Equal(ExpectedWarnings(e), ActualWarnings(enriched));

        if (e.TryGetProperty("responseBody", out JsonElement expectedBody))
        {
            Assert.NotNull(enriched.ReassembledResponse);
            GoldenJson.AssertDeepEquals(
                JsonNode.Parse(expectedBody.GetRawText()), GoldenJson.Parse(enriched.ReassembledResponse));
        }
        else
        {
            // Non-streamed cases don't synthesize a body — the wire bytes are the document.
            Assert.Null(enriched.ReassembledResponse);
        }
    }

    private static string[] ExpectedWarnings(JsonElement expected) =>
        expected.TryGetProperty("warnings", out JsonElement w)
            ? w.EnumerateArray().Select(x => x.GetString()!).ToArray()
            : [];

    private static string[] ActualWarnings(EnrichedRecord enriched) =>
        enriched.WarningsJson is null
            ? []
            : JsonSerializer.Deserialize<string[]>(enriched.WarningsJson)!;

    private static string ResponseHeaders(string? contentType, string? contentEncoding)
    {
        var headers = new JsonObject();
        if (contentType is not null)
        {
            headers["Content-Type"] = new JsonArray(contentType);
        }

        if (contentEncoding is not null)
        {
            headers["Content-Encoding"] = new JsonArray(contentEncoding);
        }

        return headers.ToJsonString();
    }

    private static byte[]? ReadIfExists(string path) => File.Exists(path) ? File.ReadAllBytes(path) : null;

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;

    private static double? Double(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.True;
}
