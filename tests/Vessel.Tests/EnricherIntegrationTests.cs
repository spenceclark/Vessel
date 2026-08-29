using System.Net;
using Vessel.Formats;
using Xunit;

namespace Vessel.Tests;

/// <summary>
/// End-to-end enrichment through proxy → writer → DB: a real parsed row lands with its
/// columns and FTS text (D1/D10), and an error row enriches from the request side alone
/// (F4). Rows are located by a unique marker since the fixture DB accumulates across tests.
/// </summary>
public class EnricherIntegrationTests(VesselFixture fx) : IClassFixture<VesselFixture>
{
    private static CancellationToken CT => TestContext.Current.CancellationToken;

    private static string Marker() => $"zap{Guid.NewGuid():N}";

    // A successful Ollama-native chat request lands fully enriched and searchable.
    [Fact]
    public async Task HappyPath_OllamaChat_EnrichedAndIndexed()
    {
        string marker = Marker();
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"{fx.VesselBaseUrl}/b/beta/api/chat?marker={marker}")
        {
            Content = new StringContent(
                $$"""{"model":"req-model","messages":[{"role":"user","content":"say {{marker}}"}]}""",
                System.Text.Encoding.UTF8, "application/json"),
        };
        using HttpResponseMessage response = await fx.Client.SendAsync(request, CT);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        CapturedRow row = await CaptureDb.WaitForRow(fx.DbPath, r => r.Path.Contains(marker));
        Assert.Equal(FormatNames.OllamaChat, row.Format);
        Assert.Equal("stub-model", row.Model);          // response model wins over request model
        Assert.Equal(5, row.TokensIn);
        Assert.Equal(3, row.TokensOut);
        Assert.Equal("stop", row.StopReason);
        Assert.NotNull(row.TokPerSec);
        Assert.InRange(row.TokPerSec!.Value, 99.5, 100.5); // eval_count 3 / eval_duration 0.03 s
        Assert.Null(row.Error);
        Assert.Empty(row.WarningCodes);

        // Response text (the marker) and prompt text are both in FTS on this row.
        Assert.Contains(row.Id, CaptureDb.FtsSearch(fx.DbPath, marker));
    }

    // F4: a dead backend still yields a browsable ollama-chat row — model and prompt from
    // the request, response-side fields null, proxy_error flagged.
    [Fact]
    public async Task ErrorRow_EnrichesFromRequestSide()
    {
        string marker = Marker();
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"{fx.VesselBaseUrl}/b/dead/api/chat?marker={marker}")
        {
            Content = new StringContent(
                $$"""{"model":"dead-model","messages":[{"role":"user","content":"reach {{marker}}"}]}""",
                System.Text.Encoding.UTF8, "application/json"),
        };
        using HttpResponseMessage response = await fx.Client.SendAsync(request, CT);
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);

        CapturedRow row = await CaptureDb.WaitForRow(fx.DbPath, r => r.Path.Contains(marker));
        Assert.Equal(FormatNames.OllamaChat, row.Format);       // detected from the path
        Assert.Equal("dead-model", row.Model);                  // from the request body
        Assert.Equal("Request", row.Error);
        Assert.Null(row.TokensOut);                             // response side stays null
        Assert.Null(row.StopReason);
        Assert.Null(row.TokPerSec);
        Assert.Contains(Warnings.ProxyError, row.WarningCodes);
        Assert.DoesNotContain(Warnings.HttpError, row.WarningCodes); // proxy failure, not a backend status

        // The prompt is still indexed for search even though the request failed.
        Assert.Contains(row.Id, CaptureDb.FtsSearch(fx.DbPath, marker));
    }
}
