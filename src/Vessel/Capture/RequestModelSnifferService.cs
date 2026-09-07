using System.Threading.Channels;

namespace Vessel.Capture;

/// <summary>
/// D5 <c>request_ready</c> — a dedicated background consumer for model-sniff jobs,
/// mirroring <see cref="CaptureChannel"/>/<see cref="CaptureWriterService"/>'s shape but
/// far lighter (no batching, nothing to write). A single always-running loop rather than
/// a fresh <c>Task.Run</c> per request avoids depending on the general thread pool's
/// thread-injection latency under load — that's what keeps <c>request_ready</c> reliably
/// ahead of <c>first_token</c> instead of racing it when the pool is busy.
/// </summary>
public sealed class RequestModelSnifferService(
    CaptureEvents events, ILogger<RequestModelSnifferService> logger) : IHostedService
{
    private readonly Channel<(long Seq, byte[]? Body)> _channel = Channel.CreateUnbounded<(long, byte[]?)>(
        new UnboundedChannelOptions { SingleReader = true });

    private Task _loop = Task.CompletedTask;

    /// <summary>
    /// #61 review — mirrors <see cref="CaptureWriterService.DrainTimeout"/>: the token
    /// <see cref="StopAsync"/> receives is shared with Kestrel's own connection drain and may
    /// already be cancelled by the time this runs, but this loop's own drain (complete the
    /// channel, let the reader exit) is near-instant once signaled, so it gets its own bounded
    /// wait rather than inheriting an already-exhausted deadline.
    /// </summary>
    public static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Fire-and-forget from the request path — never blocks, never throws out.</summary>
    public void Enqueue(long seq, byte[]? body) => _channel.Writer.TryWrite((seq, body));

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _loop = Task.Run(RunAsync, CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _channel.Writer.TryComplete();
        await _loop.WaitAsync(DrainTimeout);
    }

    private async Task RunAsync()
    {
        await foreach ((long seq, byte[]? body) in _channel.Reader.ReadAllAsync())
        {
            try
            {
                if (RequestModelSniffer.TryExtractModel(body) is string model)
                {
                    events.RequestReady(seq, model);
                }
            }
            catch (Exception ex)
            {
                // A single bad job must never take the loop down — the next request's
                // request_ready would silently stop arriving for the rest of the process.
                logger.LogDebug(ex, "request_ready sniff failed for seq {Seq}", seq);
            }
        }
    }
}
