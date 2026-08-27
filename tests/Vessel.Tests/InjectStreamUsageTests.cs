using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Vessel.Config;
using Vessel.Formats;
using Xunit;

namespace Vessel.Tests;

/// <summary>
/// F9 — the one request-path mutation (D11). When on, an eligible streamed request is
/// forwarded with <c>stream_options.include_usage</c> and a corrected Content-Length, yet
/// the stored capture is the client's original bytes plus a <c>usage_injected</c> marker.
/// Every disqualifying condition forwards the body unmodified.
/// </summary>
public class InjectStreamUsageTests
{
    private static CancellationToken CT => TestContext.Current.CancellationToken;

    private static Task<TestVessel> StartAsync(bool inject, int maxBodyMb = 32) =>
        TestVessel.StartAsync(c =>
        {
            c.Backends["stub"].Type = "openai";
            c.Backends["stub"].InjectStreamUsage = inject;
            c.Capture.MaxBodyMb = maxBodyMb;
        });

    private sealed record Reflected(string SeenBody, long? SeenContentLength);

    private static async Task<Reflected> Post(
        TestVessel vessel, string body, string marker, Action<HttpRequestMessage>? configure = null)
    {
        using var client = new HttpClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"{vessel.BaseUrl}/v1/chat/completions?marker={marker}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        configure?.Invoke(request);

        using HttpResponseMessage response = await client.SendAsync(request, CT);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string json = await response.Content.ReadAsStringAsync(CT);
        using JsonDocument doc = JsonDocument.Parse(json);
        return new Reflected(
            doc.RootElement.GetProperty("SeenBody").GetString()!,
            doc.RootElement.TryGetProperty("SeenContentLength", out JsonElement len) && len.ValueKind == JsonValueKind.Number
                ? len.GetInt64()
                : null);
    }

    private const string StreamedBody = """{"model":"m","messages":[{"role":"user","content":"hi"}],"stream":true}""";

    [Fact]
    public async Task On_EligibleRequest_ForwardsModified_StoresOriginal()
    {
        string marker = $"m{Guid.NewGuid():N}";
        await using TestVessel vessel = await StartAsync(inject: true);

        Reflected forwarded = await Post(vessel, StreamedBody, marker);

        // The backend saw the injected body with a matching (corrected) Content-Length.
        Assert.Contains("stream_options", forwarded.SeenBody);
        Assert.Contains("include_usage", forwarded.SeenBody);
        Assert.Equal(Encoding.UTF8.GetByteCount(forwarded.SeenBody), forwarded.SeenContentLength);
        Assert.NotEqual(Encoding.UTF8.GetByteCount(StreamedBody), forwarded.SeenContentLength);

        // The capture kept the client's original bytes and marked why usage appeared.
        CapturedRow row = await CaptureDb.WaitForRow(vessel.DbPath, r => r.Path.Contains(marker));
        Assert.Equal(StreamedBody, Encoding.UTF8.GetString(CaptureDb.Decompress(row.RequestBody)));
        Assert.Contains(Warnings.UsageInjected, row.WarningCodes);
    }

    [Fact]
    public async Task Off_Default_ForwardsUnmodified()
    {
        string marker = $"m{Guid.NewGuid():N}";
        await using TestVessel vessel = await StartAsync(inject: false);

        Reflected forwarded = await Post(vessel, StreamedBody, marker);

        Assert.Equal(StreamedBody, forwarded.SeenBody);
        CapturedRow row = await CaptureDb.WaitForRow(vessel.DbPath, r => r.Path.Contains(marker));
        Assert.DoesNotContain(Warnings.UsageInjected, row.WarningCodes);
    }

    [Fact]
    public async Task Skip_AlreadyHasStreamOptions()
    {
        const string body = """{"model":"m","messages":[{"role":"user","content":"hi"}],"stream":true,"stream_options":{"include_usage":true}}""";
        await AssertForwardedUnmodified(body);
    }

    [Fact]
    public async Task Skip_NotStreamed()
    {
        const string body = """{"model":"m","messages":[{"role":"user","content":"hi"}],"stream":false}""";
        await AssertForwardedUnmodified(body);
    }

    [Fact]
    public async Task Skip_NonJsonBody()
    {
        await AssertForwardedUnmodified("this is not json but mentions stream true");
    }

    private async Task AssertForwardedUnmodified(string body)
    {
        string marker = $"m{Guid.NewGuid():N}";
        await using TestVessel vessel = await StartAsync(inject: true);

        Reflected forwarded = await Post(vessel, body, marker);

        Assert.Equal(body, forwarded.SeenBody);
        CapturedRow row = await CaptureDb.WaitForRow(vessel.DbPath, r => r.Path.Contains(marker));
        Assert.DoesNotContain(Warnings.UsageInjected, row.WarningCodes);
    }

    [Fact]
    public async Task Skip_ContentEncodingPresent()
    {
        string marker = $"m{Guid.NewGuid():N}";
        await using TestVessel vessel = await StartAsync(inject: true);

        byte[] gzipped = Gzip(StreamedBody);
        using var client = new HttpClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"{vessel.BaseUrl}/v1/chat/completions?marker={marker}")
        {
            Content = new ByteArrayContent(gzipped),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Content.Headers.ContentEncoding.Add("gzip");

        using HttpResponseMessage response = await client.SendAsync(request, CT);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CT));

        // Forwarded byte-for-byte (unmodified length), never injected.
        Assert.Equal(gzipped.Length, doc.RootElement.GetProperty("SeenContentLength").GetInt64());
        CapturedRow row = await CaptureDb.WaitForRow(vessel.DbPath, r => r.Path.Contains(marker));
        Assert.DoesNotContain(Warnings.UsageInjected, row.WarningCodes);
    }

    [Fact]
    public async Task Skip_OverCap()
    {
        string marker = $"m{Guid.NewGuid():N}";
        await using TestVessel vessel = await StartAsync(inject: true, maxBodyMb: 1);

        // A >1 MB streamed body exceeds the capture cap → forwarded unmodified.
        string padding = new('a', 2 * 1024 * 1024);
        string body = $$"""{"model":"m","messages":[{"role":"user","content":"{{padding}}"}],"stream":true}""";

        Reflected forwarded = await Post(vessel, body, marker);

        Assert.Equal(Encoding.UTF8.GetByteCount(body), forwarded.SeenContentLength);
        Assert.DoesNotContain("stream_options", forwarded.SeenBody);
        CapturedRow row = await CaptureDb.WaitForRow(vessel.DbPath, r => r.Path.Contains(marker));
        Assert.DoesNotContain(Warnings.UsageInjected, row.WarningCodes);
        Assert.True(row.Truncated);
    }

    private static byte[] Gzip(string text)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            gzip.Write(bytes, 0, bytes.Length);
        }

        return output.ToArray();
    }
}
