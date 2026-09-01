using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Vessel.Proxy;

namespace Vessel.Api;

/// <summary>
/// Sends a validated replay back through this Vessel instance. The request deliberately goes
/// through the public proxy route rather than directly to an upstream backend, so replay uses
/// the ordinary capture, lifecycle, enrichment and session machinery without a second path.
/// </summary>
public sealed class ReplayExecutor(IServer server, ILogger<ReplayExecutor> logger) : IDisposable
{
    public const int MaxConcurrentReplays = 4;

    private readonly SemaphoreSlim _dispatchSlots = new(MaxConcurrentReplays, MaxConcurrentReplays);

    private readonly HttpClient _client = new(new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        AutomaticDecompression = System.Net.DecompressionMethods.None,
        UseCookies = false,
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    public void Start(ReplayPlan plan) => _ = ExecuteAsync(plan);

    private async Task ExecuteAsync(ReplayPlan plan)
    {
        await _dispatchSlots.WaitAsync();
        try
        {
            string listen = server.Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault()
                ?? throw new InvalidOperationException("Vessel listener is not available for replay");
            Uri target = BuildTarget(listen, plan.Backend, plan.Path);
            using var timeout = new CancellationTokenSource(plan.ActivityTimeout);

            using var request = new HttpRequestMessage(new HttpMethod(plan.Method), target)
            {
                Content = new ByteArrayContent(plan.Body),
            };
            if (plan.ContentType is not null)
            {
                request.Content.Headers.TryAddWithoutValidation("Content-Type", plan.ContentType);
            }
            if (plan.Accept is not null)
            {
                request.Headers.TryAddWithoutValidation("Accept", plan.Accept);
            }

            request.Headers.TryAddWithoutValidation(RouteResolver.TagsHeader, string.Join(',', plan.Tags));
            request.Headers.TryAddWithoutValidation(ProxyHandler.ReplayHeader, plan.ReplayOf.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (plan.FixupId is not null)
            {
                request.Headers.TryAddWithoutValidation(ProxyHandler.ReplayFixupsHeader, plan.FixupId);
            }
            foreach ((string name, string value) in plan.AuthHeaders)
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }

            using HttpResponseMessage response = await _client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            await using Stream stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            await stream.CopyToAsync(Stream.Null, timeout.Token);
        }
        catch (Exception ex)
        {
            // The internal request has already entered the ordinary proxy path for all normal
            // failures, which creates the capture row. This only covers executor setup or an
            // unexpected transport failure before that point; it must never fault unobserved.
            logger.LogWarning(ex, "replay execution could not complete");
        }
        finally
        {
            _dispatchSlots.Release();
        }
    }

    public static Uri BuildTarget(string listen, string backend, string path)
    {
        var builder = new UriBuilder(listen);
        if (builder.Host is "0.0.0.0" or "::" or "[::]")
        {
            builder.Host = "127.0.0.1";
        }

        return new Uri(builder.Uri, $"/b/{Uri.EscapeDataString(backend)}{path}");
    }

    public void Dispose()
    {
        _client.Dispose();
    }
}

public sealed record ReplayPlan(
    long ReplayOf,
    string Backend,
    string Method,
    string Path,
    byte[] Body,
    string? ContentType,
    string? Accept,
    string[] Tags,
    KeyValuePair<string, string>[] AuthHeaders,
    TimeSpan ActivityTimeout,
    string? FixupId = null);
