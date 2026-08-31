using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Vessel.Tests;

/// <summary>#24 — filtered, row-streamed CSV/JSONL export.</summary>
public class ExportTests
{
    private static CancellationToken CT => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CountAndExport_UseTheExactListScopeAndFilters()
    {
        await using var fx = new VesselFixture();
        await fx.InitializeAsync();

        await SendChat(fx, "run-24", "matching-export-term", "model-a");
        await SendChat(fx, "run-24", "decoy-term", "model-b");
        await SendChat(fx, "other-run", "matching-export-term", "model-a");
        await CaptureDb.WaitUntil(fx.DbPath, rows => rows.Count, count => count >= 3);

        using HttpResponseMessage sessionsResponse = await fx.Client.GetAsync(
            $"{fx.VesselBaseUrl}/vessel/api/sessions", CT);
        JsonElement sessions = await ReadJson(sessionsResponse);
        long sessionId = sessions.EnumerateArray()
            .Single(s => s.GetProperty("name").GetString() == "run-24")
            .GetProperty("id").GetInt64();
        string filters =
            $"session={sessionId}&q=matching-export-term&model=model-a&requestFormat=ollama-chat";

        using HttpResponseMessage countResponse = await fx.Client.GetAsync(
            $"{fx.VesselBaseUrl}/vessel/api/export/count?{filters}", CT);
        Assert.Equal(HttpStatusCode.OK, countResponse.StatusCode);
        Assert.Equal(1, (await ReadJson(countResponse)).GetProperty("count").GetInt64());

        using HttpResponseMessage exportResponse = await fx.Client.GetAsync(
            $"{fx.VesselBaseUrl}/vessel/api/export?format=jsonl&bodies=none&{filters}", CT);
        Assert.Equal(HttpStatusCode.OK, exportResponse.StatusCode);
        Assert.Equal("application/x-ndjson", exportResponse.Content.Headers.ContentType?.MediaType);
        Assert.Contains("vessel-run-24-", exportResponse.Content.Headers.ContentDisposition?.FileName);

        string[] lines = (await exportResponse.Content.ReadAsStringAsync(CT))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);
        using JsonDocument row = JsonDocument.Parse(lines[0]);
        Assert.Equal("model-a", row.RootElement.GetProperty("model").GetString());
        Assert.False(row.RootElement.TryGetProperty("promptText", out _));
        Assert.False(row.RootElement.TryGetProperty("requestBody", out _));
    }

    [Fact]
    public async Task BodyTiers_TextFlattensAndFullIncludesDecodedPayloads()
    {
        await using var fx = new VesselFixture();
        await fx.InitializeAsync();
        await SendChat(fx, "full export", "response-export-marker", "model-a", "prompt export marker");
        await CaptureDb.WaitForRow(
            fx.DbPath, row => row.Path.Contains("response-export-marker"));

        // There is deliberately no export-only id filter. Use the unique FTS terms, exactly
        // as the UI would, to isolate this row.
        using HttpResponseMessage textResponse = await fx.Client.GetAsync(
            $"{fx.VesselBaseUrl}/vessel/api/export?format=jsonl&bodies=text&q=prompt%20export%20marker", CT);
        JsonElement text = await ReadJsonLine(textResponse);
        Assert.Contains("prompt export marker", text.GetProperty("promptText").GetString());
        Assert.Contains("response-export-marker", text.GetProperty("responseText").GetString());
        Assert.False(text.TryGetProperty("requestBody", out _));

        using HttpResponseMessage fullResponse = await fx.Client.GetAsync(
            $"{fx.VesselBaseUrl}/vessel/api/export?format=jsonl&bodies=full&q=prompt%20export%20marker", CT);
        JsonElement full = await ReadJsonLine(fullResponse);
        Assert.Contains("prompt export marker", full.GetProperty("requestBody").GetProperty("text").GetString());
        Assert.Contains("response-export-marker", full.GetProperty("responseBody").GetProperty("text").GetString());
        Assert.Equal(JsonValueKind.Object, full.GetProperty("requestHeaders").ValueKind);
        Assert.DoesNotContain("test-secret-1234", full.GetRawText());
    }

    [Fact]
    public async Task Csv_HasSummaryColumnsAndAddsOnlyFlattenedTextAtTextTier()
    {
        await using var fx = new VesselFixture();
        await fx.InitializeAsync();
        await SendChat(fx, "csv-run", "csv-response", "csv-model", "csv prompt — em dash");
        await CaptureDb.WaitForRow(fx.DbPath, row => row.Path.Contains("csv-response"));

        using HttpResponseMessage noneResponse = await fx.Client.GetAsync(
            $"{fx.VesselBaseUrl}/vessel/api/export?format=csv&bodies=none&q=csv%20prompt", CT);
        Assert.Equal(HttpStatusCode.OK, noneResponse.StatusCode);
        Assert.Equal("text/csv", noneResponse.Content.Headers.ContentType?.MediaType);
        byte[] noneBytes = await noneResponse.Content.ReadAsByteArrayAsync(CT);
        Assert.Equal([0xEF, 0xBB, 0xBF], noneBytes[..3]);
        using var excelLikeReader = new StreamReader(
            new MemoryStream(noneBytes), Encoding.Latin1, detectEncodingFromByteOrderMarks: true);
        string none = await excelLikeReader.ReadToEndAsync(CT);
        Assert.StartsWith("id,started_at,session_id,backend,tags", none);
        Assert.DoesNotContain("prompt_text", none.Split("\r\n")[0]);

        using HttpResponseMessage textResponse = await fx.Client.GetAsync(
            $"{fx.VesselBaseUrl}/vessel/api/export?format=csv&bodies=text&q=csv%20prompt", CT);
        byte[] textBytes = await textResponse.Content.ReadAsByteArrayAsync(CT);
        Assert.Equal([0xEF, 0xBB, 0xBF], textBytes[..3]);
        using var textExcelLikeReader = new StreamReader(
            new MemoryStream(textBytes), Encoding.Latin1, detectEncodingFromByteOrderMarks: true);
        string text = await textExcelLikeReader.ReadToEndAsync(CT);
        Assert.Contains(",prompt_text,response_text\r\n", text);
        Assert.Contains("csv prompt", text);
        Assert.Contains("— em dash", text);
        Assert.Contains("csv-response", text);
    }

    [Fact]
    public async Task Jsonl_IsUtf8WithoutBom()
    {
        await using var fx = new VesselFixture();
        await fx.InitializeAsync();
        await SendChat(fx, "jsonl-run", "jsonl-no-bom", "jsonl-model");
        await CaptureDb.WaitForRow(fx.DbPath, row => row.Path.Contains("jsonl-no-bom"));

        using HttpResponseMessage response = await fx.Client.GetAsync(
            $"{fx.VesselBaseUrl}/vessel/api/export?format=jsonl&bodies=none&q=jsonl-no-bom", CT);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        byte[] bytes = await response.Content.ReadAsByteArrayAsync(CT);
        Assert.NotEmpty(bytes);
        Assert.Equal((byte)'{', bytes[0]);
        Assert.False(bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }));
    }

    [Theory]
    [InlineData("format=xml&bodies=none")]
    [InlineData("format=jsonl&bodies=huge")]
    [InlineData("format=csv&bodies=full")]
    public async Task InvalidOptions_ReturnMarked400(string query)
    {
        await using var fx = new VesselFixture();
        await fx.InitializeAsync();
        using HttpResponseMessage response = await fx.Client.GetAsync(
            $"{fx.VesselBaseUrl}/vessel/api/export?{query}", CT);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", response.Headers.GetValues("X-Vessel-Error").Single());
    }

    private static async Task SendChat(
        VesselFixture fx, string session, string marker, string model, string prompt = "default prompt")
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{fx.VesselBaseUrl}/api/chat?marker={Uri.EscapeDataString(marker)}&model={Uri.EscapeDataString(model)}");
        request.Headers.TryAddWithoutValidation("X-Vessel-Session", session);
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer test-secret-1234");
        request.Content = JsonContent.Create(new
        {
            model,
            messages = new[] { new { role = "user", content = prompt } },
        });
        using HttpResponseMessage response = await fx.Client.SendAsync(request, CT);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<JsonElement> ReadJson(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync(CT);
        using JsonDocument doc = JsonDocument.Parse(body);
        return doc.RootElement.Clone();
    }

    private static async Task<JsonElement> ReadJsonLine(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync(CT);
        string line = Assert.Single(body.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        using JsonDocument doc = JsonDocument.Parse(line);
        return doc.RootElement.Clone();
    }
}
