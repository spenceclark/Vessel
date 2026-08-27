using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace Vessel.Tests;

/// <summary>
/// Phase 1 capture assertions (spec §3, C1–C8/C11) against the shared fixture. Rows
/// are located by a unique query-string marker since the fixture's DB accumulates
/// rows across the class. C2 (streaming stays unbuffered through the tees) is
/// covered by T7 in <see cref="ProxyIntegrationTests"/>, which now runs with capture on.
/// </summary>
public class CaptureIntegrationTests(VesselFixture fx) : IClassFixture<VesselFixture>
{
    private static CancellationToken CT => TestContext.Current.CancellationToken;

    private static string Marker() => $"m{Guid.NewGuid():N}";

    // C1: the tee alters nothing, and what it stored decompresses byte-identical.
    [Fact]
    public async Task C1_TeeFidelity_BodiesStoredByteIdentical()
    {
        byte[] body = new byte[128 * 1024];
        Random.Shared.NextBytes(body);
        body[0] = 0xC3; // invalid UTF-8 lead-in
        body[1] = 0x28;
        body[2] = 0xFF;

        string marker = Marker();
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"{fx.VesselBaseUrl}/b/beta/echo?{marker}")
        {
            Content = new ByteArrayContent(body),
        };

        using HttpResponseMessage response = await fx.Client.SendAsync(request, CT);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        byte[] responseBytes = await response.Content.ReadAsByteArrayAsync(CT);

        // The stub saw the exact bytes (tee did not alter the forwarded request)...
        using JsonDocument echo = JsonDocument.Parse(responseBytes);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(body)),
            echo.RootElement.GetProperty("BodySha256").GetString(),
            ignoreCase: true);

        // ...and both stored bodies decompress to exactly what was on the wire.
        CapturedRow row = await CaptureDb.WaitForRow(fx.DbPath, r => r.Path.Contains(marker));
        Assert.Equal(body, CaptureDb.Decompress(row.RequestBody));
        Assert.Equal(responseBytes, CaptureDb.Decompress(row.ResponseBody));
        Assert.False(row.Truncated);
        Assert.Null(row.ResponseRaw);
        Assert.False(row.Streamed);
        Assert.Equal("raw", row.Format);
        Assert.Equal("POST", row.Method);
        Assert.Equal(200, row.StatusCode);
        Assert.Null(row.Error);
    }

    // C4: duration/ttft/overhead sanity — streamed request.
    [Fact]
    public async Task C4_Timings_Streamed()
    {
        string marker = Marker();
        using HttpResponseMessage response = await fx.Client.GetAsync(
            $"{fx.VesselBaseUrl}/b/beta/sse?n=4&delayMs=150&{marker}", CT);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await response.Content.ReadAsByteArrayAsync(CT);

        CapturedRow row = await CaptureDb.WaitForRow(fx.DbPath, r => r.Path.Contains(marker));
        Assert.True(row.Streamed);
        Assert.NotNull(row.DurationMs);
        Assert.True(row.DurationMs >= 3 * 150, $"duration {row.DurationMs} ms < stream span");
        Assert.NotNull(row.TtftMs);
        Assert.InRange(row.TtftMs.Value, 0, row.DurationMs.Value / 2);
        Assert.NotNull(row.VesselOverheadMs);
        Assert.InRange(row.VesselOverheadMs.Value, 0, 500);

        // Streamed: raw chunk stream stored, reassembly deferred to Phase 2.
        Assert.Null(row.ResponseBody);
        string raw = System.Text.Encoding.UTF8.GetString(CaptureDb.Decompress(row.ResponseRaw));
        Assert.Contains("chunk-0", raw);
        Assert.Contains("chunk-3", raw);
    }

    // C4: non-streamed → ttft is NULL per §4.2.
    [Fact]
    public async Task C4_Timings_NonStreamed_TtftNull()
    {
        string marker = Marker();
        using HttpResponseMessage response = await fx.Client.GetAsync($"{fx.VesselBaseUrl}/b/beta/echo?{marker}", CT);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        CapturedRow row = await CaptureDb.WaitForRow(fx.DbPath, r => r.Path.Contains(marker));
        Assert.False(row.Streamed);
        Assert.Null(row.TtftMs);
        Assert.NotNull(row.DurationMs);
        Assert.NotNull(row.VesselOverheadMs);
    }

    // C6: unrecognized traffic is captured silently as raw — proxied untouched.
    [Fact]
    public async Task C6_RawFallback_GarbageStillCaptured()
    {
        string marker = Marker();
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"{fx.VesselBaseUrl}/completely/unknown/endpoint?{marker}")
        {
            Content = new StringContent("this is {not: json,,,"),
        };

        using HttpResponseMessage response = await fx.Client.SendAsync(request, CT);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); // stub catch-all answers

        CapturedRow row = await CaptureDb.WaitForRow(fx.DbPath, r => r.Path.Contains(marker));
        Assert.Equal("raw", row.Format);
        Assert.Equal("alpha", row.Backend); // default backend
        Assert.Equal(
            "this is {not: json,,,",
            System.Text.Encoding.UTF8.GetString(CaptureDb.Decompress(row.RequestBody)));
    }

    // C7: Vessel-generated errors land as rows with the error code.
    [Fact]
    public async Task C7_ErrorRows_UnknownBackendAndUnreachable()
    {
        string marker1 = Marker();
        using HttpResponseMessage notFound = await fx.Client.GetAsync($"{fx.VesselBaseUrl}/b/nope/echo?{marker1}", CT);
        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);

        CapturedRow unknownRow = await CaptureDb.WaitForRow(fx.DbPath, r => r.Path.Contains(marker1));
        Assert.Equal("unknown_backend", unknownRow.Error);
        Assert.Equal("nope", unknownRow.Backend);
        Assert.Equal(404, unknownRow.StatusCode);
        // The Vessel error body itself is captured.
        Assert.Contains("unknown_backend",
            System.Text.Encoding.UTF8.GetString(CaptureDb.Decompress(unknownRow.ResponseBody)));

        string marker2 = Marker();
        using HttpResponseMessage badGateway = await fx.Client.GetAsync($"{fx.VesselBaseUrl}/b/dead/echo?{marker2}", CT);
        Assert.Equal(HttpStatusCode.BadGateway, badGateway.StatusCode);

        CapturedRow deadRow = await CaptureDb.WaitForRow(fx.DbPath, r => r.Path.Contains(marker2));
        Assert.Equal("upstream_unreachable", deadRow.Error);
        Assert.Equal("dead", deadRow.Backend);
        Assert.Equal(502, deadRow.StatusCode);
    }

    // C8: concurrent requests all land — no loss, no duplicates.
    [Fact]
    public async Task C8_ConcurrentRequests_AllCaptured()
    {
        const int count = 100;
        string marker = Marker();

        await Task.WhenAll(Enumerable.Range(0, count).Select(async i =>
        {
            using HttpResponseMessage response = await fx.Client.GetAsync(
                $"{fx.VesselBaseUrl}/b/beta/echo?{marker}&i={i}", CT);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }));

        List<CapturedRow> rows = await CaptureDb.WaitUntil(
            fx.DbPath,
            rows => rows.Where(r => r.Path.Contains(marker)).ToList(),
            matched => matched.Count >= count);

        Assert.Equal(count, rows.Count);
        Assert.Equal(count, rows.Select(r => r.Path).Distinct().Count());
    }

    // C11: routing detail lands in the right columns; path is the forward path + query.
    [Fact]
    public async Task C11_BackendTagsPath_Columns()
    {
        string marker = Marker();
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"{fx.VesselBaseUrl}/b/beta/t/planner,run42/echo?{marker}");
        request.Headers.TryAddWithoutValidation("X-Vessel-Tags", "extra");
        using HttpResponseMessage response = await fx.Client.SendAsync(request, CT);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        CapturedRow row = await CaptureDb.WaitForRow(fx.DbPath, r => r.Path.Contains(marker));
        Assert.Equal("beta", row.Backend);
        Assert.Equal($"/echo?{marker}", row.Path);
        Assert.NotNull(row.Tags);
        string?[] tags = JsonDocument.Parse(row.Tags).RootElement.EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(["planner", "run42", "extra"], tags);
    }

    // C3: over-cap bodies are truncated in storage only — the wire is untouched.
    [Fact]
    public async Task C3_Truncation_StoredCopyCapped_TrafficIntact()
    {
        await using TestVessel vessel = await TestVessel.StartAsync(c => c.Capture.MaxBodyMb = 1);
        using var client = new HttpClient();

        const int capBytes = 1024 * 1024;

        // Request side: 1.5 MB body through a 1 MB cap.
        byte[] body = new byte[capBytes + capBytes / 2];
        Random.Shared.NextBytes(body);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{vessel.BaseUrl}/echo?req")
        {
            Content = new ByteArrayContent(body),
        };
        using HttpResponseMessage response = await client.SendAsync(request, CT);
        using JsonDocument echo = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(CT));
        Assert.Equal(body.Length, echo.RootElement.GetProperty("BodyLength").GetInt64()); // stub saw it all

        CapturedRow requestRow = await CaptureDb.WaitForRow(vessel.DbPath, r => r.Path.Contains("req"));
        Assert.True(requestRow.Truncated);
        byte[] stored = CaptureDb.Decompress(requestRow.RequestBody);
        Assert.Equal(capBytes, stored.Length);
        Assert.Equal(body.AsSpan(0, capBytes).ToArray(), stored);

        // Response side: 1.5 MB response through the same cap.
        byte[] bigResponse = await client.GetByteArrayAsync($"{vessel.BaseUrl}/big?bytes={body.Length}&resp", CT);
        Assert.Equal(body.Length, bigResponse.Length); // client got it all

        CapturedRow responseRow = await CaptureDb.WaitForRow(vessel.DbPath, r => r.Path.Contains("resp"));
        Assert.True(responseRow.Truncated);
        Assert.Equal(capBytes, CaptureDb.Decompress(responseRow.ResponseBody).Length);
    }
}
