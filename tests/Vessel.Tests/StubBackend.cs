using System.Net;
using System.Security.Cryptography;
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

        app.Map("/sse", (RequestDelegate)(context =>
            StreamChunks(context, "text/event-stream", i => $"data: chunk-{i}\n\n")));

        app.Map("/ndjson", (RequestDelegate)(context =>
            StreamChunks(context, "application/x-ndjson", i => $"{{\"i\":{i}}}\n")));

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

        context.Response.ContentType = contentType;
        try
        {
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
