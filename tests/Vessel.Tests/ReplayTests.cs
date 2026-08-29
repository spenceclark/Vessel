using System.Net;
using System.Net.Http.Json;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Vessel.Config;
using Xunit;

namespace Vessel.Tests;

/// <summary>Phase 5 P1–P6 core replay coverage against a real internal self-request.</summary>
public sealed class ReplayTests
{
    private static CancellationToken CT => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Replay_ValidatesUnknownRowsTargetsAndFormatBeforeStartingWork()
    {
        await using TestVessel vessel = await TestVessel.StartAsync(config =>
            config.Backends["other"] = new() { BaseUrl = VesselPlaceholder, Type = "ollama" });
        using var client = new HttpClient();

        using HttpResponseMessage missing = await client.PostAsJsonAsync(
            $"{vessel.BaseUrl}/vessel/api/requests/999/replay", new { }, CT);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        long rawId = await CaptureRaw(vessel, client);
        using HttpResponseMessage unknown = await client.PostAsJsonAsync(
            $"{vessel.BaseUrl}/vessel/api/requests/{rawId}/replay", new { backend = "missing" }, CT);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);

        using HttpResponseMessage mismatch = await client.PostAsJsonAsync(
            $"{vessel.BaseUrl}/vessel/api/requests/{rawId}/replay", new { backend = "other" }, CT);
        Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);
        Assert.Equal("format_mismatch", mismatch.Headers.GetValues("X-Vessel-Error").Single());
        await Task.Delay(50, CT);
        Assert.Empty((await GetReplays(client, vessel.BaseUrl, rawId)).EnumerateArray());
    }

    [Fact]
    public async Task Replay_ReusesProxyPipeline_PreservesTags_AndStampsReplayOf()
    {
        await using TestVessel vessel = await TestVessel.StartAsync();
        using var client = new HttpClient();
        const string body = "{\"model\":\"original-model\",\"messages\":[{\"role\":\"user\",\"content\":\"hello replay\"}]}";

        using var originalRequest = new HttpRequestMessage(HttpMethod.Post, $"{vessel.BaseUrl}/api/chat?replay-case")
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };
        originalRequest.Headers.TryAddWithoutValidation("X-Vessel-Tags", "agent,phase5");
        using HttpResponseMessage originalResponse = await client.SendAsync(originalRequest, CT);
        Assert.Equal(HttpStatusCode.OK, originalResponse.StatusCode);

        CapturedRow original = await CaptureDb.WaitForRow(vessel.DbPath, row => row.Path.Contains("replay-case"));
        using HttpResponseMessage accepted = await client.PostAsJsonAsync(
            $"{vessel.BaseUrl}/vessel/api/requests/{original.Id}/replay", new { model = "qwen-test" }, CT);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);

        JsonElement replay = await WaitForReplay(client, vessel.BaseUrl, original.Id);
        Assert.Equal(original.Id, replay.GetProperty("replayOf").GetInt64());
        Assert.Equal(new[] { "agent", "phase5" }, replay.GetProperty("tags").EnumerateArray().Select(x => x.GetString()).ToArray());

        long replayId = replay.GetProperty("id").GetInt64();
        using HttpResponseMessage detailResponse = await client.GetAsync($"{vessel.BaseUrl}/vessel/api/requests/{replayId}", CT);
        using JsonDocument detail = JsonDocument.Parse(await detailResponse.Content.ReadAsStringAsync(CT));
        string replayBody = detail.RootElement.GetProperty("requestBody").GetProperty("text").GetString()!;
        Assert.Contains("\"model\":\"qwen-test\"", replayBody);
        Assert.DoesNotContain("Authorization", detail.RootElement.GetProperty("requestHeaders").ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Replay_MissingConfiguredAuthEnvironmentNamesTheVariable()
    {
        const string env = "VESSEL_TEST_MISSING_REPLAY_KEY";
        Environment.SetEnvironmentVariable(env, null);
        await using TestVessel vessel = await TestVessel.StartAsync(config =>
        {
            config.Backends["secured"] = new() { BaseUrl = "http://127.0.0.1:1", Type = "openai", AuthEnv = env };
        });
        using var client = new HttpClient();
        // Raw rows deliberately cannot change target, so create an OpenAI-wire capture.
        using HttpResponseMessage sourceResponse = await client.PostAsync(
            $"{vessel.BaseUrl}/v1/chat/completions?auth-case", new StringContent("{\"model\":\"m\",\"messages\":[]}"), CT);
        CapturedRow source = await CaptureDb.WaitForRow(vessel.DbPath, row => row.Path.Contains("auth-case"));
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            $"{vessel.BaseUrl}/vessel/api/requests/{source.Id}/replay", new { backend = "secured" }, CT);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("missing_replay_auth", response.Headers.GetValues("X-Vessel-Error").Single());
        Assert.Contains(env, await response.Content.ReadAsStringAsync(CT));
        await Task.Delay(50, CT);
        Assert.Empty((await GetReplays(client, vessel.BaseUrl, source.Id)).EnumerateArray());
    }

    [Fact]
    public async Task Replay_ReattachesConfiguredOpenAiAndAnthropicAuthWithoutPersistingItInHeaders()
    {
        string openAiEnv = $"VESSEL_TEST_OPENAI_{Guid.NewGuid():N}";
        string anthropicEnv = $"VESSEL_TEST_ANTHROPIC_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(openAiEnv, "openai-test-secret");
        Environment.SetEnvironmentVariable(anthropicEnv, "anthropic-test-secret");
        try
        {
            await using TestVessel vessel = await TestVessel.StartAsync(config =>
            {
                config.Backends["openai-secured"] = new() { BaseUrl = VesselPlaceholder, Type = "openai", AuthEnv = openAiEnv };
                config.Backends["anthropic-secured"] = new() { BaseUrl = VesselPlaceholder, Type = "anthropic", AuthEnv = anthropicEnv };
            });
            // The callback cannot refer to Stub, so point the live config at it before issuing requests.
            VesselConfig config = vessel.Services.GetRequiredService<ConfigStore>().Current;
            config.Backends["openai-secured"].BaseUrl = vessel.Stub.BaseUrl;
            config.Backends["anthropic-secured"].BaseUrl = vessel.Stub.BaseUrl;
            vessel.Services.GetRequiredService<ConfigStore>().Apply(config);

            using var client = new HttpClient();
            long openAiId = await CaptureJson(client, vessel, "/v1/chat/completions?auth-openai", "{\"model\":\"m\",\"messages\":[]}");
            long anthropicId = await CaptureJson(client, vessel, "/v1/messages?auth-anthropic", "{\"model\":\"m\",\"max_tokens\":1,\"messages\":[]}");

            await Replay(client, vessel, openAiId, "openai-secured");
            await Replay(client, vessel, anthropicId, "anthropic-secured");
            JsonElement openAiReplay = await WaitForReplay(client, vessel.BaseUrl, openAiId);
            JsonElement anthropicReplay = await WaitForReplay(client, vessel.BaseUrl, anthropicId);

            string openAiHeaders = await DetailText(client, vessel.BaseUrl, openAiReplay.GetProperty("id").GetInt64(), "requestHeaders");
            string anthropicHeaders = await DetailText(client, vessel.BaseUrl, anthropicReplay.GetProperty("id").GetInt64(), "requestHeaders");
            ReflectPayload openAiWire = await ReplayReflect(client, vessel.BaseUrl, openAiReplay);
            ReflectPayload anthropicWire = await ReplayReflect(client, vessel.BaseUrl, anthropicReplay);
            Assert.True(openAiWire.HasAuthorization);
            Assert.False(openAiWire.HasAnthropicApiKey);
            Assert.False(anthropicWire.HasAuthorization);
            Assert.True(anthropicWire.HasAnthropicApiKey);
            Assert.Equal("2023-06-01", anthropicWire.AnthropicVersion);
            Assert.DoesNotContain("openai-test-secret", openAiHeaders);
            Assert.Contains("Authorization", openAiHeaders);
            Assert.DoesNotContain("anthropic-test-secret", anthropicHeaders);
            Assert.Contains("x-api-key", anthropicHeaders, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Authorization", anthropicHeaders, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable(openAiEnv, null);
            Environment.SetEnvironmentVariable(anthropicEnv, null);
        }
    }

    [Fact]
    public async Task Replay_CompatibilityMatrix_IsEnforcedWithoutDispatchingRejectedTargets()
    {
        await using TestVessel vessel = await TestVessel.StartAsync(config =>
        {
            config.Backends["openai"] = new() { BaseUrl = VesselPlaceholder, Type = "openai" };
            config.Backends["anthropic"] = new() { BaseUrl = VesselPlaceholder, Type = "anthropic" };
            config.Backends["ollama"] = new() { BaseUrl = VesselPlaceholder, Type = "ollama" };
            config.Backends["other-auto"] = new() { BaseUrl = VesselPlaceholder, Type = "auto" };
        });
        PointAllBackendsAtStub(vessel);
        using var client = new HttpClient();

        (string Format, long Id, string[] Allowed)[] rows =
        [
            ("openai-chat", await CaptureJson(client, vessel, "/v1/chat/completions?matrix-chat", "{\"model\":\"m\",\"messages\":[]}"), ["stub", "openai", "ollama"]),
            ("openai-responses", await CaptureJson(client, vessel, "/v1/responses?matrix-responses", "{\"model\":\"m\",\"input\":[]}"), ["stub", "openai"]),
            ("anthropic-messages", await CaptureJson(client, vessel, "/v1/messages?matrix-anthropic", "{\"model\":\"m\",\"max_tokens\":1,\"messages\":[]}"), ["stub", "anthropic", "ollama"]),
            ("ollama-chat", await CaptureJson(client, vessel, "/api/chat?matrix-ollama-chat", "{\"model\":\"m\",\"messages\":[]}"), ["stub", "ollama"]),
            ("ollama-generate", await CaptureJson(client, vessel, "/api/generate?matrix-ollama-generate", "{\"model\":\"m\",\"prompt\":\"x\"}"), ["stub", "ollama"]),
            ("raw", await CaptureRaw(vessel, client), ["stub"]),
        ];

        string[] targets = ["stub", "openai", "anthropic", "ollama", "other-auto"];
        foreach ((string format, long id, string[] allowed) in rows)
        {
            int replayCount = 0;
            foreach (string target in targets)
            {
                using HttpResponseMessage response = await client.PostAsJsonAsync(
                    $"{vessel.BaseUrl}/vessel/api/requests/{id}/replay", new { backend = target }, CT);
                if (allowed.Contains(target))
                {
                    Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
                    replayCount++;
                    await WaitForReplayCount(client, vessel.BaseUrl, id, replayCount);
                }
                else
                {
                    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                    Assert.Equal("format_mismatch", response.Headers.GetValues("X-Vessel-Error").Single());
                    await Task.Delay(50, CT);
                    Assert.Equal(replayCount, (await GetReplays(client, vessel.BaseUrl, id)).GetArrayLength());
                }
            }

            Assert.Equal(format, (await GetDetail(client, vessel.BaseUrl, id)).GetProperty("format").GetString());
        }

        long rawId = rows.Single(row => row.Format == "raw").Id;
        int before = (await GetReplays(client, vessel.BaseUrl, rawId)).GetArrayLength();
        using HttpResponseMessage rawOverride = await client.PostAsJsonAsync(
            $"{vessel.BaseUrl}/vessel/api/requests/{rawId}/replay", new { model = "changed" }, CT);
        Assert.Equal(HttpStatusCode.BadRequest, rawOverride.StatusCode);
        await Task.Delay(50, CT);
        Assert.Equal(before, (await GetReplays(client, vessel.BaseUrl, rawId)).GetArrayLength());
    }

    [Fact]
    public async Task Replay_DecodesGzip_DropsStaleHeaders_UsesCurrentSession_AndOnlyChangesModel()
    {
        await using TestVessel vessel = await TestVessel.StartAsync(config => config.Backends["stub"].Type = "openai");
        using var client = new HttpClient();
        const string originalJson = "{\"model\":\"before\",\"messages\":[{\"role\":\"user\",\"content\":\"gzip replay\"}],\"temperature\":0.25}";
        byte[] compressed;
        using (var output = new MemoryStream())
        {
            using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                await gzip.WriteAsync(Encoding.UTF8.GetBytes(originalJson), CT);
            }
            compressed = output.ToArray();
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{vessel.BaseUrl}/v1/chat/completions?gzip-replay")
        {
            Content = new ByteArrayContent(compressed),
        };
        request.Content.Headers.ContentType = new("application/json");
        request.Content.Headers.ContentEncoding.Add("gzip");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer original-secret");
        request.Headers.TryAddWithoutValidation("X-Stale-Header", "must-not-replay");
        request.Headers.TryAddWithoutValidation("X-Vessel-Tags", "gzip,phase5");
        using HttpResponseMessage originalResponse = await client.SendAsync(request, CT);
        CapturedRow original = await CaptureDb.WaitForRow(vessel.DbPath, row => row.Path.Contains("gzip-replay"));
        JsonElement originalBefore = await GetDetail(client, vessel.BaseUrl, original.Id);

        using HttpResponseMessage sessionResponse = await client.PostAsJsonAsync(
            $"{vessel.BaseUrl}/vessel/api/sessions", new { name = "replay session" }, CT);
        using JsonDocument session = JsonDocument.Parse(await sessionResponse.Content.ReadAsStringAsync(CT));
        long currentSession = session.RootElement.GetProperty("id").GetInt64();

        using HttpResponseMessage accepted = await client.PostAsJsonAsync(
            $"{vessel.BaseUrl}/vessel/api/requests/{original.Id}/replay", new { model = "after" }, CT);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        JsonElement replay = await WaitForReplay(client, vessel.BaseUrl, original.Id);
        Assert.Equal(currentSession, replay.GetProperty("sessionId").GetInt64());
        Assert.Equal(["gzip", "phase5"], replay.GetProperty("tags").EnumerateArray().Select(value => value.GetString()));

        ReflectPayload wire = await ReplayReflect(client, vessel.BaseUrl, replay);
        Assert.False(wire.HasAuthorization);
        Assert.False(wire.HasAnthropicApiKey);
        Assert.False(wire.HasStaleHeader);
        using JsonDocument wireBody = JsonDocument.Parse(wire.SeenBody);
        Assert.Equal("after", wireBody.RootElement.GetProperty("model").GetString());
        Assert.Equal(0.25, wireBody.RootElement.GetProperty("temperature").GetDouble());
        Assert.Equal("gzip replay", wireBody.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());

        JsonElement originalAfter = await GetDetail(client, vessel.BaseUrl, original.Id);
        Assert.Equal(originalBefore.GetProperty("requestBody").GetRawText(), originalAfter.GetProperty("requestBody").GetRawText());
    }

    [Fact]
    public async Task Replay_RejectsUnparseableCaptureTruncatedAndDecodeFailedBodiesWithoutDispatch()
    {
        await using TestVessel vessel = await TestVessel.StartAsync(config =>
        {
            config.Backends["stub"].Type = "openai";
            config.Capture.MaxBodyMb = 1;
        });
        using var client = new HttpClient();

        long unparseable = await CaptureJson(client, vessel, "/v1/chat/completions?bad-json-replay", "not-json");
        using HttpResponseMessage badJson = await client.PostAsJsonAsync(
            $"{vessel.BaseUrl}/vessel/api/requests/{unparseable}/replay", new { model = "changed" }, CT);
        Assert.Equal(HttpStatusCode.BadRequest, badJson.StatusCode);

        string oversized = "{\"model\":\"m\",\"messages\":[],\"padding\":\"" + new string('x', 1024 * 1024) + "\"}";
        long truncated = await CaptureJson(client, vessel, "/v1/chat/completions?truncated-replay", oversized);
        using HttpResponseMessage truncatedResponse = await client.PostAsJsonAsync(
            $"{vessel.BaseUrl}/vessel/api/requests/{truncated}/replay", new { }, CT);
        Assert.Equal(HttpStatusCode.BadRequest, truncatedResponse.StatusCode);
        Assert.Contains("truncated", await truncatedResponse.Content.ReadAsStringAsync(CT));

        using var invalidEncoded = new HttpRequestMessage(HttpMethod.Post, $"{vessel.BaseUrl}/v1/chat/completions?decode-failed-replay")
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes("not-a-gzip-stream")),
        };
        invalidEncoded.Content.Headers.ContentType = new("application/json");
        invalidEncoded.Content.Headers.ContentEncoding.Add("gzip");
        using HttpResponseMessage invalidEncodedResponse = await client.SendAsync(invalidEncoded, CT);
        CapturedRow decodeFailed = await CaptureDb.WaitForRow(vessel.DbPath, row => row.Path.Contains("decode-failed-replay"));
        using HttpResponseMessage decodeFailedReplay = await client.PostAsJsonAsync(
            $"{vessel.BaseUrl}/vessel/api/requests/{decodeFailed.Id}/replay", new { }, CT);
        Assert.Equal(HttpStatusCode.BadRequest, decodeFailedReplay.StatusCode);
        Assert.Contains("content-decoded", await decodeFailedReplay.Content.ReadAsStringAsync(CT));

        Assert.Empty((await GetReplays(client, vessel.BaseUrl, unparseable)).EnumerateArray());
        Assert.Empty((await GetReplays(client, vessel.BaseUrl, truncated)).EnumerateArray());
        Assert.Empty((await GetReplays(client, vessel.BaseUrl, decodeFailed.Id)).EnumerateArray());
    }

    [Fact]
    public async Task Replay_StreamDrainCompletesEnrichment_AndConcurrentReplaysCorrelate()
    {
        await using TestVessel vessel = await TestVessel.StartAsync(config => config.Backends["stub"].Type = "ollama");
        using var client = new HttpClient();
        long original = await CaptureJson(
            client, vessel, "/api/chat?stream=1&marker=replay-stream", "{\"model\":\"m\",\"messages\":[],\"stream\":true}");

        Task<HttpResponseMessage>[] starts = Enumerable.Range(0, 2).Select(_ => client.PostAsJsonAsync(
            $"{vessel.BaseUrl}/vessel/api/requests/{original}/replay", new { }, CT)).ToArray();
        HttpResponseMessage[] accepted = await Task.WhenAll(starts);
        Assert.All(accepted, response => Assert.Equal(HttpStatusCode.Accepted, response.StatusCode));
        foreach (HttpResponseMessage response in accepted)
        {
            response.Dispose();
        }

        JsonElement replays = await WaitForReplayCount(client, vessel.BaseUrl, original, 2);
        Assert.All(replays.EnumerateArray(), replay =>
        {
            Assert.Equal(original, replay.GetProperty("replayOf").GetInt64());
            Assert.True(replay.GetProperty("streamed").GetBoolean());
            Assert.Equal(2, replay.GetProperty("tokensOut").GetInt64());
            Assert.True(replay.GetProperty("tokPerSec").GetDouble() > 0);
        });
    }

    [Fact]
    public async Task Replay_LocalAnthropicTarget_OmitsAuth()
    {
        string? prior = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
        try
        {
            await using TestVessel vessel = await TestVessel.StartAsync(config => config.Backends["stub"].Type = "anthropic");
            using var client = new HttpClient();
            long original = await CaptureJson(client, vessel, "/v1/messages?local-anthropic", "{\"model\":\"m\",\"max_tokens\":1,\"messages\":[]}");

            await Replay(client, vessel, original, "stub");
            ReflectPayload wire = await ReplayReflect(client, vessel.BaseUrl, await WaitForReplay(client, vessel.BaseUrl, original));
            Assert.False(wire.HasAuthorization);
            Assert.False(wire.HasAnthropicApiKey);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", prior);
        }
    }

    [Theory]
    [InlineData("http://0.0.0.0:4550", "http://127.0.0.1:4550/b/stub/api/chat")]
    [InlineData("http://[::]:4550", "http://127.0.0.1:4550/b/stub/api/chat")]
    [InlineData("http://127.0.0.1:4550", "http://127.0.0.1:4550/b/stub/api/chat")]
    public void ReplayExecutor_NormalizesWildcardListeners(string listen, string expected)
    {
        Assert.Equal(expected, Vessel.Api.ReplayExecutor.BuildTarget(listen, "stub", "/api/chat").AbsoluteUri);
    }

    private static void PointAllBackendsAtStub(TestVessel vessel)
    {
        ConfigStore store = vessel.Services.GetRequiredService<ConfigStore>();
        VesselConfig config = store.Current;
        foreach (BackendConfig backend in config.Backends.Values)
        {
            backend.BaseUrl = vessel.Stub.BaseUrl;
        }

        store.Apply(config);
    }

    private static async Task<ReflectPayload> ReplayReflect(HttpClient client, string baseUrl, JsonElement replay)
    {
        JsonElement detail = await GetDetail(client, baseUrl, replay.GetProperty("id").GetInt64());
        string text = detail.GetProperty("responseBody").GetProperty("text").GetString()!;
        return JsonSerializer.Deserialize<ReflectPayload>(text, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        })!;
    }

    private static async Task<JsonElement> GetDetail(HttpClient client, string baseUrl, long id)
    {
        using HttpResponseMessage response = await client.GetAsync($"{baseUrl}/vessel/api/requests/{id}", CT);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CT));
        return doc.RootElement.Clone();
    }

    private static async Task<JsonElement> GetReplays(HttpClient client, string baseUrl, long originalId)
    {
        using HttpResponseMessage response = await client.GetAsync($"{baseUrl}/vessel/api/requests/{originalId}/replays", CT);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CT));
        return doc.RootElement.Clone();
    }

    private static async Task<JsonElement> WaitForReplayCount(HttpClient client, string baseUrl, long originalId, int count)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            JsonElement replays = await GetReplays(client, baseUrl, originalId);
            if (replays.GetArrayLength() >= count)
            {
                return replays;
            }

            await Task.Delay(50, CT);
        }

        Assert.Fail($"{count} replay captures did not appear");
        return default;
    }

    private static async Task<long> CaptureRaw(TestVessel vessel, HttpClient client)
    {
        using HttpResponseMessage response = await client.PostAsync(
            $"{vessel.BaseUrl}/echo?raw-replay", new StringContent("raw captured body"), CT);
        CapturedRow row = await CaptureDb.WaitForRow(vessel.DbPath, r => r.Path.Contains("raw-replay"));
        return row.Id;
    }

    private static async Task<long> CaptureJson(HttpClient client, TestVessel vessel, string path, string body)
    {
        using HttpResponseMessage response = await client.PostAsync(vessel.BaseUrl + path, new StringContent(body), CT);
        CapturedRow row = await CaptureDb.WaitForRow(vessel.DbPath, r => r.Path.Contains(path[(path.IndexOf('?') + 1)..]));
        return row.Id;
    }

    private static async Task Replay(HttpClient client, TestVessel vessel, long id, string backend)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            $"{vessel.BaseUrl}/vessel/api/requests/{id}/replay", new { backend }, CT);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    private static async Task<string> DetailText(HttpClient client, string baseUrl, long id, string property)
    {
        using HttpResponseMessage response = await client.GetAsync($"{baseUrl}/vessel/api/requests/{id}", CT);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CT));
        return doc.RootElement.GetProperty(property).ToString();
    }

    private static async Task<JsonElement> WaitForReplay(HttpClient client, string baseUrl, long originalId)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            using HttpResponseMessage response = await client.GetAsync($"{baseUrl}/vessel/api/requests/{originalId}/replays", CT);
            using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CT));
            if (doc.RootElement.GetArrayLength() > 0)
            {
                return doc.RootElement[0].Clone();
            }

            await Task.Delay(50, CT);
        }

        Assert.Fail("replay capture did not appear");
        return default;
    }

    // TestVessel creates its stub after the config callback, so this route is never reached:
    // validation rejects the raw-format target before a connection is attempted.
    private const string VesselPlaceholder = "http://127.0.0.1:1";
}
