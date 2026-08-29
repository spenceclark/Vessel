using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace Vessel.Tests;

public class ProxyIntegrationTests(VesselFixture fx) : IClassFixture<VesselFixture>
{
    private static CancellationToken CT => TestContext.Current.CancellationToken;

    private static async Task<EchoPayload> GetEcho(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using Stream stream = await response.Content.ReadAsStreamAsync(CT);
        return (await JsonSerializer.DeserializeAsync<EchoPayload>(
            stream, new JsonSerializerOptions(JsonSerializerDefaults.Web), CT))!;
    }

    // T1: body bytes, method, path, query arrive at the stub unmodified —
    // including a binary body with invalid-UTF8 bytes.
    [Fact]
    public async Task T1_BodyMethodPathQuery_ArriveUnmodified()
    {
        byte[] body = new byte[64 * 1024];
        Random.Shared.NextBytes(body);
        body[0] = 0xC3; // invalid UTF-8 lead-in
        body[1] = 0x28;
        body[2] = 0xFF;

        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"{fx.VesselBaseUrl}/b/beta/echo?a=1&b=two%20words")
        {
            Content = new ByteArrayContent(body),
        };

        EchoPayload echo = await GetEcho(await fx.Client.SendAsync(request, CT));

        Assert.Equal("beta", echo.ServerId);
        Assert.Equal("POST", echo.Method);
        Assert.Equal("/echo", echo.Path);
        Assert.Equal("?a=1&b=two%20words", echo.Query);
        Assert.Equal(body.Length, echo.BodyLength);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(body)), echo.BodySha256);
    }

    // T2: all client headers arrive except X-Vessel-*; nothing unexpected is added.
    [Fact]
    public async Task T2_HeadersForwarded_ExceptVesselControlPlane_NothingAdded()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{fx.VesselBaseUrl}/echo");
        request.Headers.TryAddWithoutValidation("X-Custom", "hello");
        request.Headers.TryAddWithoutValidation("X-Vessel-Backend", "beta");
        request.Headers.TryAddWithoutValidation("X-Vessel-Tags", "planner,run42");

        EchoPayload echo = await GetEcho(await fx.Client.SendAsync(request, CT));

        Assert.Equal("beta", echo.ServerId);
        Assert.Equal("hello", echo.Headers["X-Custom"]);
        Assert.DoesNotContain(echo.Headers.Keys,
            k => k.StartsWith("X-Vessel-", StringComparison.OrdinalIgnoreCase));

        // Forward-as-is means nothing beyond what the client sent (Host is rewritten
        // to the backend's, standard reverse-proxy behavior).
        string[] unexpected = echo.Headers.Keys
            .Where(k => k is not ("Host" or "X-Custom"))
            .ToArray();
        Assert.Empty(unexpected);
        Assert.Equal(new Uri(fx.Beta.BaseUrl).Authority, echo.Headers["Host"]);
    }

    // T3: response status, headers, and body return to the client unmodified.
    [Fact]
    public async Task T3_ResponseFidelity_StatusHeadersBodyUnmodified()
    {
        using HttpResponseMessage direct = await fx.Client.GetAsync($"{fx.Beta.BaseUrl}/respond", CT);
        using HttpResponseMessage proxied = await fx.Client.GetAsync($"{fx.VesselBaseUrl}/b/beta/respond", CT);

        Assert.Equal((HttpStatusCode)418, proxied.StatusCode);
        Assert.Equal(await direct.Content.ReadAsByteArrayAsync(CT), await proxied.Content.ReadAsByteArrayAsync(CT));
        Assert.Equal(Enumerable.Range(0, 256).Select(i => (byte)i).ToArray(),
            await proxied.Content.ReadAsByteArrayAsync(CT));

        static Dictionary<string, string> Comparable(HttpResponseMessage response)
        {
            string[] ignore = ["Date", "Server", "Transfer-Encoding", "Connection", "Keep-Alive"];
            return response.Headers.Concat(response.Content.Headers)
                .Where(h => !ignore.Contains(h.Key, StringComparer.OrdinalIgnoreCase))
                .ToDictionary(h => h.Key, h => string.Join(",", h.Value));
        }

        Assert.Equal(Comparable(direct), Comparable(proxied));
        Assert.Equal("hello-from-stub", proxied.Headers.GetValues("X-Stub-Custom").Single());
        Assert.Equal(["one", "two"], proxied.Headers.GetValues("X-Stub-Multi"));
    }

    // T4: /b/{name} prefix routes to the right stub and is stripped from the path.
    [Theory]
    [InlineData("/b/beta/echo", "beta", "/echo")]
    [InlineData("/b/BETA/echo", "beta", "/echo")]
    [InlineData("/b/alpha/echo", "alpha", "/echo")]
    [InlineData("/b/beta", "beta", "/")]
    [InlineData("/b/beta/", "beta", "/")]
    [InlineData("/echo", "alpha", "/echo")]
    public async Task T4_PathPrefix_RoutesAndStrips(string path, string expectedServer, string expectedPath)
    {
        EchoPayload echo = await GetEcho(await fx.Client.GetAsync($"{fx.VesselBaseUrl}{path}", CT));
        Assert.Equal(expectedServer, echo.ServerId);
        Assert.Equal(expectedPath, echo.Path);
    }

    // T5: header routes; path prefix beats header when both are present.
    [Fact]
    public async Task T5_HeaderRouting_PathPrefixWins()
    {
        using var byHeader = new HttpRequestMessage(HttpMethod.Get, $"{fx.VesselBaseUrl}/echo");
        byHeader.Headers.TryAddWithoutValidation("X-Vessel-Backend", "beta");
        Assert.Equal("beta", (await GetEcho(await fx.Client.SendAsync(byHeader, CT))).ServerId);

        using var both = new HttpRequestMessage(HttpMethod.Get, $"{fx.VesselBaseUrl}/b/alpha/echo");
        both.Headers.TryAddWithoutValidation("X-Vessel-Backend", "beta");
        Assert.Equal("alpha", (await GetEcho(await fx.Client.SendAsync(both, CT))).ServerId);
    }

    // T6: unknown backend → 404, marked as a Vessel error, listing valid backends.
    [Fact]
    public async Task T6_UnknownBackend_404WithMarkedJson()
    {
        using HttpResponseMessage response = await fx.Client.GetAsync($"{fx.VesselBaseUrl}/b/nope/echo", CT);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("unknown_backend", response.Headers.GetValues("X-Vessel-Error").Single());

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CT));
        JsonElement error = doc.RootElement.GetProperty("error");
        Assert.Equal("vessel", error.GetProperty("source").GetString());
        Assert.Equal("unknown_backend", error.GetProperty("code").GetString());
        Assert.Contains("nope", error.GetProperty("message").GetString());
        string?[] backends = error.GetProperty("backends").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(["alpha", "beta", "dead"], backends);
    }

    // T7: streaming is unbuffered — chunks arrive as they are sent, not at the end.
    // The credibility test for the whole product.
    [Theory]
    [InlineData("sse")]
    [InlineData("ndjson")]
    public async Task T7_Streaming_IsUnbuffered(string kind)
    {
        const int chunks = 5;
        const int delayMs = 200;

        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"{fx.VesselBaseUrl}/b/beta/{kind}?n={chunks}&delayMs={delayMs}");
        using HttpResponseMessage response = await fx.Client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, CT);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var stopwatch = Stopwatch.StartNew();
        var arrivals = new List<long>();
        await using Stream stream = await response.Content.ReadAsStreamAsync(CT);
        using var body = new MemoryStream();
        byte[] buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, CT)) > 0)
        {
            arrivals.Add(stopwatch.ElapsedMilliseconds);
            body.Write(buffer, 0, read);
        }

        string text = System.Text.Encoding.UTF8.GetString(body.ToArray());
        for (int i = 0; i < chunks; i++)
        {
            Assert.Contains(kind == "sse" ? $"chunk-{i}" : $"\"i\":{i}", text);
        }

        // Buffered would deliver everything at once after ~(chunks-1)*delayMs.
        // Unbuffered: the first chunk arrives while later chunks are still unsent,
        // and arrivals are spread over most of the stream's duration.
        Assert.True(arrivals.Count >= 3,
            $"expected ≥3 distinct reads, got {arrivals.Count} — chunks were coalesced (buffering?)");
        Assert.True(arrivals[0] < (chunks - 1) * delayMs / 2,
            $"first chunk arrived at {arrivals[0]} ms — after later chunks were sent (buffering?)");
        long spread = arrivals[^1] - arrivals[0];
        Assert.True(spread >= (chunks - 1) * delayMs / 2,
            $"arrivals spread over {spread} ms, expected ≥{(chunks - 1) * delayMs / 2} ms (buffering?)");
    }

    // T8: upstream dies mid-stream → client connection aborted, no fabricated clean end.
    [Fact]
    public async Task T8_UpstreamDiesMidStream_ClientConnectionAborted()
    {
        using HttpResponseMessage healthy = await fx.Client.GetAsync($"{fx.VesselBaseUrl}/b/beta/echo?health-before-die", CT);
        Assert.Equal(HttpStatusCode.OK, healthy.StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{fx.VesselBaseUrl}/b/beta/die");
        using HttpResponseMessage response = await fx.Client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, CT);

        await using Stream stream = await response.Content.ReadAsStreamAsync(CT);
        var received = new MemoryStream();

        Exception? failure = null;
        try
        {
            byte[] buffer = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(buffer, CT)) > 0)
            {
                received.Write(buffer, 0, read);
            }
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        Assert.Contains("data: first", System.Text.Encoding.UTF8.GetString(received.ToArray()));
        Assert.NotNull(failure);
        Assert.True(failure is IOException or HttpRequestException,
            $"expected an aborted-connection error, got {failure.GetType()}: {failure.Message}");

        CapturedRow row = await CaptureDb.WaitForRow(fx.DbPath, captured => captured.Path == "/die");
        Assert.Equal("ResponseBodyDestination", row.Error);

        using HttpResponseMessage statusResponse = await fx.Client.GetAsync($"{fx.VesselBaseUrl}/vessel/api/status", CT);
        using JsonDocument status = JsonDocument.Parse(await statusResponse.Content.ReadAsStringAsync(CT));
        JsonElement beta = status.RootElement.GetProperty("backends").EnumerateArray()
            .Single(backend => backend.GetProperty("name").GetString() == "beta");
        Assert.Equal("green", beta.GetProperty("health").GetProperty("state").GetString());
    }

    // T9: backend unreachable (closed port) → 502 upstream_unreachable.
    [Fact]
    public async Task T9_UnreachableBackend_502WithMarkedJson()
    {
        using HttpResponseMessage response = await fx.Client.GetAsync($"{fx.VesselBaseUrl}/b/dead/echo", CT);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("upstream_unreachable", response.Headers.GetValues("X-Vessel-Error").Single());

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CT));
        Assert.Equal("vessel", doc.RootElement.GetProperty("error").GetProperty("source").GetString());
        Assert.Equal("upstream_unreachable", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    // T10: /vessel/api/status reports version, listen address, backends + default.
    [Fact]
    public async Task T10_Status_ReportsVersionListenAndBackends()
    {
        using HttpResponseMessage response = await fx.Client.GetAsync($"{fx.VesselBaseUrl}/vessel/api/status", CT);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CT));
        JsonElement root = doc.RootElement;
        Assert.False(string.IsNullOrEmpty(root.GetProperty("version").GetString()));
        Assert.Equal(fx.VesselBaseUrl, root.GetProperty("listen").GetString());
        Assert.Equal("alpha", root.GetProperty("defaultBackend").GetString());

        var backends = root.GetProperty("backends").EnumerateArray()
            .ToDictionary(
                b => b.GetProperty("name").GetString()!,
                b => b.GetProperty("default").GetBoolean());
        Assert.Equal(["alpha", "beta", "dead"], backends.Keys.Order().ToArray());
        Assert.True(backends["alpha"]);
        Assert.False(backends["beta"]);
    }

    // Activity timeout (D3/D5): zero bytes moving for longer than the configured
    // activity timeout → 504 upstream_timeout.
    [Fact]
    public async Task T11_ActivityTimeout_504WithMarkedJson()
    {
        using HttpResponseMessage response = await fx.Client.GetAsync(
            $"{fx.ShortTimeoutBaseUrl}/b/beta/slow-headers?ms=10000", CT);

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.Equal("upstream_timeout", response.Headers.GetValues("X-Vessel-Error").Single());

        using HttpResponseMessage statusResponse = await fx.Client.GetAsync(
            $"{fx.ShortTimeoutBaseUrl}/vessel/api/status", CT);
        using JsonDocument status = JsonDocument.Parse(await statusResponse.Content.ReadAsStringAsync(CT));
        JsonElement beta = status.RootElement.GetProperty("backends").EnumerateArray()
            .Single(backend => backend.GetProperty("name").GetString() == "beta");
        Assert.Equal("red", beta.GetProperty("health").GetProperty("state").GetString());
    }

    // Reserved namespace: /vessel/* is never proxied. Unknown API paths still get the
    // marked JSON 404 (D7); non-API paths serve the embedded UI (or its placeholder when
    // none is embedded, as in this test binary) instead of proxying anywhere.
    [Fact]
    public async Task T12_VesselNamespace_IsNeverProxied()
    {
        using HttpResponseMessage apiResponse = await fx.Client.GetAsync($"{fx.VesselBaseUrl}/vessel/api/does-not-exist", CT);
        Assert.Equal(HttpStatusCode.NotFound, apiResponse.StatusCode);
        Assert.Equal("not_found", apiResponse.Headers.GetValues("X-Vessel-Error").Single());

        using HttpResponseMessage uiResponse = await fx.Client.GetAsync($"{fx.VesselBaseUrl}/vessel/does-not-exist", CT);
        Assert.False(uiResponse.Headers.Contains("X-Vessel-Error")); // never Vessel's marked error path
        string body = await uiResponse.Content.ReadAsStringAsync(CT);
        Assert.DoesNotContain("ServerId", body); // never reached a backend's echo

        // But the same path is reachable on a backend via an explicit prefix.
        EchoPayload echo = await GetEcho(await fx.Client.GetAsync($"{fx.VesselBaseUrl}/b/beta/echo", CT));
        Assert.Equal("beta", echo.ServerId);
    }
}
