using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Vessel.Tests;

/// <summary>
/// In-proc Kestrel stub backend on a random port. Endpoints prove forward-as-is
/// (/echo), streaming pass-through (/sse, /ndjson), timeout behavior (/slow-headers),
/// mid-stream upstream death (/die), and response fidelity (/respond).
/// </summary>
public sealed class StubBackend : IAsyncDisposable
{
    private readonly WebApplication _app;

    public string Id { get; }

    public string BaseUrl { get; }

    private StubBackend(WebApplication app, string id)
    {
        _app = app;
        Id = id;
        BaseUrl = app.Urls.First();
    }

    public static async Task<StubBackend> StartAsync(string id)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Limits.MaxRequestBodySize = null;
            kestrel.Listen(IPAddress.Loopback, 0);
        });

        var app = builder.Build();

        app.Map("/echo", (RequestDelegate)(async context =>
        {
            using var body = new MemoryStream();
            await context.Request.Body.CopyToAsync(body);

            var payload = new EchoPayload(
                id,
                context.Request.Method,
                context.Request.Path.Value ?? "",
                context.Request.QueryString.Value ?? "",
                context.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString()),
                Convert.ToHexString(SHA256.HashData(body.ToArray())),
                body.Length);

            context.Response.ContentType = "application/json";
            await JsonSerializer.SerializeAsync(context.Response.Body, payload);
        }));

        app.Map("/respond", (RequestDelegate)(async context =>
        {
            byte[] bytes = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
            context.Response.StatusCode = 418;
            context.Response.ContentType = "application/octet-stream";
            context.Response.Headers["X-Stub-Custom"] = "hello-from-stub";
            context.Response.Headers["X-Stub-Multi"] = new[] { "one", "two" };
            context.Response.ContentLength = bytes.Length;
            await context.Response.Body.WriteAsync(bytes);
        }));

        // A canned Ollama-native chat response whose assistant content echoes the
        // ?marker= query, so enrichment tests can locate the row and search its text.
        // ?stream=1 switches to a canned streamed NDJSON reply instead (wire-true to the
        // ollama-chat/streamed-basic golden fixture) — path must stay literally "/api/chat"
        // for format detection, so the choice is a query flag rather than a second route.
        app.Map("/api/chat", (RequestDelegate)(async context =>
        {
            await context.Request.Body.CopyToAsync(Stream.Null);

            if (context.Request.Query["stream"] == "1")
            {
                context.Response.ContentType = "application/x-ndjson";

                // R08 — an optional pause between chunks so a test can disconnect *after*
                // real streamed content arrived but before the stream finishes; and an
                // optional marker in the first chunk so FTS assertions have a unique term.
                int chunkDelayMs = int.TryParse(context.Request.Query["delayMs"], out int cd) ? cd : 0;
                // Trailing space when a marker is used so it stays its own FTS token once
                // the chunks fold; without a marker the default folds to "Hello" exactly as
                // the other streaming tests expect.
                string streamMarker = context.Request.Query["marker"].ToString();
                string firstContent = streamMarker.Length > 0 ? streamMarker + " " : "He";

                string[] lines =
                [
                    $$"""{"model":"qwen2.5:1.5b","created_at":"2026-08-27T00:00:00.100000Z","message":{"role":"assistant","content":"{{firstContent}}"},"done":false}""",
                    """{"model":"qwen2.5:1.5b","created_at":"2026-08-27T00:00:00.200000Z","message":{"role":"assistant","content":"llo"},"done":false}""",
                    """{"model":"qwen2.5:1.5b","created_at":"2026-08-27T00:00:00.300000Z","message":{"role":"assistant","content":""},"done_reason":"stop","done":true,"total_duration":500000000,"load_duration":30000000,"prompt_eval_count":10,"prompt_eval_duration":100000000,"eval_count":2,"eval_duration":40000000}""",
                ];

                try
                {
                    for (int i = 0; i < lines.Length; i++)
                    {
                        await context.Response.WriteAsync(lines[i] + "\n", context.RequestAborted);
                        await context.Response.Body.FlushAsync(context.RequestAborted);
                        if (chunkDelayMs > 0 && i < lines.Length - 1)
                        {
                            await Task.Delay(chunkDelayMs, context.RequestAborted);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Client went away mid-stream — exactly the case under test.
                }

                return;
            }

            string marker = context.Request.Query["marker"].ToString();
            // FilterTests (D1/D2) needs distinct `model` values to seed — an optional
            // override, defaulting to "stub-model" for every existing caller.
            string model = context.Request.Query["model"].ToString() is { Length: > 0 } m ? m : "stub-model";
            string body =
                $$"""
                {"model":"{{model}}","message":{"role":"assistant","content":"{{marker}}"},"done_reason":"stop","done":true,"total_duration":100000000,"load_duration":10000000,"prompt_eval_count":5,"prompt_eval_duration":20000000,"eval_count":3,"eval_duration":30000000}
                """;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(body);
        }));

        // Reflects the request body it received (text + declared Content-Length), so the
        // injectStreamUsage tests can assert exactly what was forwarded upstream.
        RequestDelegate reflectRequest = async context =>
        {
            using var received = new MemoryStream();
            await context.Request.Body.CopyToAsync(received);
            var payload = new ReflectPayload(
                Encoding.UTF8.GetString(received.ToArray()),
                context.Request.ContentLength,
                context.Request.Headers.ContainsKey("Authorization"),
                context.Request.Headers.ContainsKey("x-api-key"),
                context.Request.Headers["anthropic-version"].FirstOrDefault(),
                context.Request.Headers.ContainsKey("X-Stale-Header"));
            context.Response.ContentType = "application/json";
            await JsonSerializer.SerializeAsync(context.Response.Body, payload);
        };
        app.Map("/v1/chat/completions", reflectRequest);
        app.Map("/v1/messages", reflectRequest);

        // D01/R05 — a gzip-encoded JSON response. ?bomb=1 makes the *decoded* size huge from
        // a tiny wire body (highly compressible zeros), which is how the decode budget gets
        // exercised end to end; otherwise it's an ordinary small compressed chat completion.
        app.Map("/gzip", (RequestDelegate)(async context =>
        {
            await context.Request.Body.CopyToAsync(Stream.Null);

            byte[] payload = context.Request.Query["bomb"] == "1"
                ? new byte[4 * 1024 * 1024]
                : Encoding.UTF8.GetBytes(
                    """{"id":"gz","object":"chat.completion","model":"gzip-model","choices":[{"index":0,"message":{"role":"assistant","content":"compressed hello"},"finish_reason":"stop"}],"usage":{"prompt_tokens":4,"completion_tokens":2,"total_tokens":6}}""");

            using var compressed = new MemoryStream();
            using (var gzip = new System.IO.Compression.GZipStream(compressed, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            {
                gzip.Write(payload, 0, payload.Length);
            }

            byte[] wire = compressed.ToArray();
            context.Response.ContentType = "application/json";
            context.Response.Headers.ContentEncoding = "gzip";
            context.Response.ContentLength = wire.Length;
            await context.Response.Body.WriteAsync(wire);
        }));

        app.Map("/sse", (RequestDelegate)(context =>
            StreamChunks(context, "text/event-stream", i => $"data: chunk-{i}\n\n")));

        app.Map("/ndjson", (RequestDelegate)(context =>
            StreamChunks(context, "application/x-ndjson", i => $"{{\"i\":{i}}}\n")));

        app.Map("/big", (RequestDelegate)(async context =>
        {
            // Deterministic but incompressible bytes, for truncation and DB-size tests.
            int bytes = int.TryParse(context.Request.Query["bytes"], out int bv) ? bv : 1024;
            byte[] data = new byte[bytes];
            new Random(12345).NextBytes(data);
            context.Response.ContentType = "application/octet-stream";
            context.Response.ContentLength = bytes;
            await context.Response.Body.WriteAsync(data);
        }));

        app.Map("/slow-headers", (RequestDelegate)(async context =>
        {
            int ms = int.TryParse(context.Request.Query["ms"], out int v) ? v : 3000;
            try
            {
                await Task.Delay(ms, context.RequestAborted);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await context.Response.WriteAsync("late");
        }));

        app.Map("/die", (RequestDelegate)(async context =>
        {
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync("data: first\n\n");
            await context.Response.Body.FlushAsync();
            await Task.Delay(150);
            context.Abort();
        }));

        // Catch-all so bare-backend routes ("/b/{name}" → "/") are observable.
        app.Map("/{**rest}", (RequestDelegate)(async context =>
        {
            context.Response.ContentType = "application/json";
            var payload = new EchoPayload(
                id, context.Request.Method, context.Request.Path.Value ?? "",
                context.Request.QueryString.Value ?? "", new Dictionary<string, string>(), "", 0);
            await JsonSerializer.SerializeAsync(context.Response.Body, payload);
        }));

        await app.StartAsync();
        return new StubBackend(app, id);
    }

    private static async Task StreamChunks(HttpContext context, string contentType, Func<int, string> chunk)
    {
        int n = int.TryParse(context.Request.Query["n"], out int nv) ? nv : 5;
        int delayMs = int.TryParse(context.Request.Query["delayMs"], out int dv) ? dv : 200;
        // Opt-in only (defaults to 0, i.e. today's instant-first-byte behavior): a delay
        // before the *first* chunk too, for tests that need a realistic TTFT — a warm
        // loopback connection can otherwise answer in well under a millisecond, faster
        // than any real backend, which starves anything racing to beat first_token.
        int initialDelayMs = int.TryParse(context.Request.Query["initialDelayMs"], out int iv) ? iv : 0;

        context.Response.ContentType = contentType;
        try
        {
            if (initialDelayMs > 0)
            {
                await Task.Delay(initialDelayMs, context.RequestAborted);
            }

            for (int i = 0; i < n; i++)
            {
                await context.Response.WriteAsync(chunk(i), context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
                if (i < n - 1)
                {
                    await Task.Delay(delayMs, context.RequestAborted);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync(TimeSpan.FromSeconds(2));
        await _app.DisposeAsync();
    }
}

public sealed record EchoPayload(
    string ServerId,
    string Method,
    string Path,
    string Query,
    Dictionary<string, string> Headers,
    string BodySha256,
    long BodyLength);

public sealed record ReflectPayload(
    string SeenBody,
    long? SeenContentLength,
    bool HasAuthorization = false,
    bool HasAnthropicApiKey = false,
    string? AnthropicVersion = null,
    bool HasStaleHeader = false);
