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

        // H0b(1) — the hello frame is the first thing on the wire, before any lifecycle frame,
        // so the client learns this process's run id up front. On a reconnect that lands on a
        // *restarted* Vessel, the new run id tells the client its in-flight seqs belong to a
        // dead process and must be discarded wholesale (a watermark comparison can't tell that:
        // an old high seq sits above the fresh process's low watermark and looks "just
        // started"). Deliberately carries no `id:` field, so it never perturbs the client's
        // gap-detection watermark. It bypasses the hub (written straight to the response), so
        // it is always ordered before any channel frame the loop below reads.
        await context.Response.WriteAsync($"event: hello\ndata: {{\"serverRunId\":\"{hub.RunId}\"}}\n\n", aborted);
        await context.Response.Body.FlushAsync(aborted);

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

                // R11 — the `id:` field carries the hub's publish sequence. A client that
                // sees it jump knows its bounded queue dropped frames (drop-oldest is
                // deliberate) and can reconcile, instead of leaving in-flight rows running
                // forever because a `completed` was silently lost.
                await context.Response.WriteAsync(
                    $"id: {evt.Id}\nevent: {evt.Name}\ndata: {evt.Json}\n\n", aborted);
                await context.Response.Body.FlushAsync(aborted);
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected — nothing more to do; the subscription disposes below.
        }
    }
}
