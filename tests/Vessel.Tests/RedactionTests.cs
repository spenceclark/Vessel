using System.Net;
using System.Text.Json;
using Vessel.Capture;
using Xunit;

namespace Vessel.Tests;

/// <summary>C5: redaction unit coverage plus the "no plaintext key anywhere in the file" integration check.</summary>
public class RedactionTests(VesselFixture fx) : IClassFixture<VesselFixture>
{
    private static CancellationToken CT => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("Bearer sk-abc123456789xyzw", "Bearer …xyzw")]
    [InlineData("Basic dXNlcjpwYXNzd29yZA==", "Basic …ZA==")]
    [InlineData("raw-api-key-1234567890", "…7890")]
    [InlineData("Bearer short", "Bearer …")] // ≤ 8-char secret: tail omitted entirely
    [InlineData("tiny", "…")]
    [InlineData("", "…")]
    public void Redact_SchemePlusLastFour(string value, string expected) =>
        Assert.Equal(expected, HeaderRedactor.Redact(value));

    [Fact]
    public void ToRedactedJson_CoversAllSecretHeaders_CaseInsensitive_OthersUntouched()
    {
        var headers = new Microsoft.AspNetCore.Http.HeaderDictionary
        {
            ["authorization"] = "Bearer sk-secret-123456789",
            ["X-API-KEY"] = "key-secret-987654321",
            ["Api-Key"] = "another-secret-key-11",
            ["COOKIE"] = "session=deadbeefcafe1234",
            ["Proxy-Authorization"] = "Basic cHJveHlzZWNyZXQxMjM0",
            ["Set-Cookie"] = new Microsoft.Extensions.Primitives.StringValues(
                ["sid=cookiesecret1; Path=/", "b=othercookiesecret2"]),
            ["Content-Type"] = "application/json",
            ["X-Custom"] = "plain-visible-value",
        };

        string json = HeaderRedactor.ToRedactedJson(headers);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);

        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        Assert.Equal("Bearer …6789", root.GetProperty("authorization")[0].GetString());
        Assert.Equal("…4321", root.GetProperty("X-API-KEY")[0].GetString());
        Assert.Equal("…y-11", root.GetProperty("Api-Key")[0].GetString());
        Assert.Equal("…1234", root.GetProperty("COOKIE")[0].GetString());
        Assert.Equal("Basic …MjM0", root.GetProperty("Proxy-Authorization")[0].GetString());
        Assert.Equal("…th=/", root.GetProperty("Set-Cookie")[0].GetString());
        Assert.Equal("…ret2", root.GetProperty("Set-Cookie")[1].GetString());
        Assert.Equal("application/json", root.GetProperty("Content-Type")[0].GetString());
        Assert.Equal("plain-visible-value", root.GetProperty("X-Custom")[0].GetString());
    }

    // The end-to-end guarantee: the secret is forwarded intact but appears nowhere in
    // the database file — not in headers, not via any other path.
    [Fact]
    public async Task SecretHeader_ForwardedIntact_NeverPersistedInPlaintext()
    {
        const string secret = "sk-vessel-test-plaintext-canary-8f3a2b";
        string marker = $"m{Guid.NewGuid():N}";

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{fx.VesselBaseUrl}/b/beta/respond?{marker}");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {secret}");
        using HttpResponseMessage response = await fx.Client.SendAsync(request, CT);
        Assert.Equal((HttpStatusCode)418, response.StatusCode); // proxied fine

        CapturedRow row = await CaptureDb.WaitForRow(fx.DbPath, r => r.Path.Contains(marker));
        using JsonDocument headers = JsonDocument.Parse(row.RequestHeaders);
        Assert.Equal("Bearer …3a2b", headers.RootElement.GetProperty("Authorization")[0].GetString());
        Assert.DoesNotContain(secret, row.RequestHeaders);

        // Scan the raw bytes of the DB file and its WAL (shared read — SQLite still
        // holds a write handle).
        byte[] secretBytes = System.Text.Encoding.UTF8.GetBytes(secret);
        foreach (string file in Directory.GetFiles(Path.GetDirectoryName(fx.DbPath)!)
                     .Where(f => f.StartsWith(fx.DbPath, StringComparison.Ordinal)))
        {
            using var stream = new FileStream(
                file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory, CT);
            Assert.True(memory.ToArray().AsSpan().IndexOf(secretBytes) < 0,
                $"plaintext secret found in {Path.GetFileName(file)}");
        }
    }
}
