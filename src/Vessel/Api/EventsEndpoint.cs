using Vessel.Capture;

namespace Vessel.Api;

/// <summary>
/// D5 — <c>GET /vessel/api/events</c>: SSE lifecycle feed. No replay; the UI loads history
/// via REST after subscribing and reconciles by id/seq. A comment heartbeat every 15 s
/// keeps idle connections (and intermediary proxies) alive without a fake event.
/// </summary>
public static class EventsEndpoint
{
    private static readonly TimeSpan _heartbeatInterval = TimeSpan.FromSeconds(15);

    public static async Task Handle(HttpContext context)
    {
        var hub = context.RequestServices.GetRequiredService<CaptureEvents>();

        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        context.Response.ContentType = "text/event-stream";
        await context.Response.Body.FlushAsync(context.RequestAborted);

        CancellationToken aborted = context.RequestAborted;
        using CaptureSubscription subscription = hub.Subscribe();

        try
        {
            while (!aborted.IsCancellationRequested)
            {
                using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(aborted);
                timeoutCts.CancelAfter(_heartbeatInterval);

                SseEvent evt;
                try
                {
                    evt = await subscription.Reader.ReadAsync(timeoutCts.Token);
                }
                catch (OperationCanceledException) when (!aborted.IsCancellationRequested)
                {
                    await context.Response.WriteAsync(": ping\n\n", aborted);
                    await context.Response.Body.FlushAsync(aborted);
                    continue;
                }

                await context.Response.WriteAsync($"event: {evt.Name}\ndata: {evt.Json}\n\n", aborted);
                await context.Response.Body.FlushAsync(aborted);
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected — nothing more to do; the subscription disposes below.
        }
    }
}
