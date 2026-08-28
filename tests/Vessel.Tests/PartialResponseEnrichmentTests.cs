using System.Net;
using System.Text.Json;
using Xunit;

namespace Vessel.Tests;

/// <summary>
/// R08 — a transport failure after real streamed content arrived must not discard that
/// content. <c>hasRealResponse = record.Error is null</c> treated every proxy error as if
/// no upstream response existed, so a mid-stream disconnect lost its partial reassembly,
/// <c>response_text</c> and FTS row — contradicting phase-2 D4's partial-stream behaviour.
/// The wire bytes were always kept; what disappeared was the useful derived view.
/// </summary>
public class PartialResponseEnrichmentTests
{
    private static CancellationToken CT => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ClientDisconnectMidStream_KeepsReassemblyResponseTextAndFts()
    {
        await using TestVessel vessel = await TestVessel.StartAsync();
        using var client = new HttpClient();

        const string marker = "zzpartialneedle";

        // Read the first NDJSON chunk, then drop the connection while the stream is still
        // open — a real partial upstream response, not a pre-response failure.
        // A real Ollama chat call: POST carrying the model, which is where the adapter
        // falls back for `model` when a truncated stream never delivers the final object.
        using (var request = new HttpRequestMessage(
                   HttpMethod.Post, $"{vessel.BaseUrl}/api/chat?stream=1&delayMs=1500&marker={marker}")
               {
                   Content = new StringContent(
                       """{"model":"qwen2.5:1.5b","messages":[{"role":"user","content":"hi"}],"stream":true}""",
                       System.Text.Encoding.UTF8,
                       "application/json"),
               })
        {
            using HttpResponseMessage response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, CT);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            await using Stream stream = await response.Content.ReadAsStreamAsync(CT);
            byte[] buffer = new byte[4096];
            int read = await stream.ReadAsync(buffer, CT);
            Assert.True(read > 0, "expected the first streamed chunk before disconnecting");
            Assert.Contains(marker, System.Text.Encoding.UTF8.GetString(buffer, 0, read));
        }

        CapturedRow row = await CaptureDb.WaitForRow(vessel.DbPath, r => r.Path.Contains(marker));

        // The transport failure is still recorded — this is not about hiding the error.
        Assert.Equal("client_disconnect", row.Error);
        Assert.Contains("client_disconnect", row.WarningCodes);
        // ...and the partial stream is still recognized as one, so both coexist.
        Assert.Contains("stream_incomplete", row.WarningCodes);

        // Format detection and the adapter both ran, despite the error.
        Assert.Equal("ollama-chat", row.Format);
        Assert.Equal("qwen2.5:1.5b", row.Model);

        // The derived view the review found missing: reassembled body + searchable text.
        JsonElement detail = await GetJson(client, $"{vessel.BaseUrl}/vessel/api/requests/{row.Id}", CT);
        string reassembled = detail.GetProperty("responseBody").GetProperty("text").GetString()!;
        Assert.Contains(marker, reassembled);

        JsonElement found = await GetJson(
            client, $"{vessel.BaseUrl}/vessel/api/requests?q={marker}", CT);
        long[] ids = found.GetProperty("rows").EnumerateArray()
            .Select(r => r.GetProperty("id").GetInt64())
            .ToArray();
        Assert.Contains(row.Id, ids);
    }

    /// <summary>
    /// The other side of the same rule: when Vessel wrote the body itself there is no
    /// upstream response to parse, and its error JSON must not be mistaken for one.
    /// </summary>
    [Fact]
    public async Task PreResponseFailure_DoesNotParseVesselsOwnErrorBody()
    {
        await using TestVessel vessel = await TestVessel.StartAsync();
        using var client = new HttpClient();

        using HttpResponseMessage resp = await client.GetAsync($"{vessel.BaseUrl}/b/nope/api/chat?unknownbackendcase", CT);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);

        CapturedRow row = await CaptureDb.WaitForRow(vessel.DbPath, r => r.Path.Contains("unknownbackendcase"));

        Assert.Equal("unknown_backend", row.Error);
        Assert.Contains("proxy_error", row.WarningCodes);
        // Vessel's error document is not a completion: nothing is extracted from it.
        Assert.Null(row.Model);
        Assert.Null(row.TokensOut);
        Assert.Null(row.StopReason);
    }

    private static async Task<JsonElement> GetJson(HttpClient client, string url, CancellationToken ct)
    {
        using HttpResponseMessage response = await client.GetAsync(url, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string text = await response.Content.ReadAsStringAsync(ct);
        using JsonDocument doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }
}
