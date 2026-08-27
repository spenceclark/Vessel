using System.Threading.Channels;
using Vessel.Storage;

namespace Vessel.Capture;

/// <summary>
/// The background writer (§6.1): initializes the database before Kestrel starts
/// accepting traffic (registered ahead of the server's hosted service, so a broken DB
/// fails startup fast), then consumes the channel in batches — one transaction per
/// batch, flushed at 64 records or 250 ms after the batch's first record, whichever
/// comes first. Retention runs after each batch. Shutdown completes the channel and
/// drains what's left.
/// </summary>
public sealed class CaptureWriterService(
    CaptureChannel channel, SqliteCaptureStore store, ILogger<CaptureWriterService> logger) : IHostedService
{
    public const int MaxBatchSize = 64;

    public static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(250);

    private Task _loop = Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        store.Initialize();
        _loop = Task.Run(RunAsync, CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        channel.Complete();
        await _loop.WaitAsync(cancellationToken);
    }

    private async Task RunAsync()
    {
        ChannelReader<CaptureRecord> reader = channel.Reader;
        var batch = new List<CaptureRecord>(MaxBatchSize);

        try
        {
            while (await reader.WaitToReadAsync())
            {
                batch.Clear();
                Task deadline = Task.Delay(FlushInterval);

                while (batch.Count < MaxBatchSize)
                {
                    while (batch.Count < MaxBatchSize && reader.TryRead(out CaptureRecord? record))
                    {
                        batch.Add(record);
                    }

                    if (batch.Count >= MaxBatchSize)
                    {
                        break;
                    }

                    Task<bool> more = reader.WaitToReadAsync().AsTask();
                    if (await Task.WhenAny(more, deadline) == deadline || !await more)
                    {
                        break;
                    }
                }

                Flush(batch);
            }

            // Channel completed: drain anything that raced in.
            batch.Clear();
            while (reader.TryRead(out CaptureRecord? record))
            {
                batch.Add(record);
                if (batch.Count >= MaxBatchSize)
                {
                    Flush(batch);
                    batch.Clear();
                }
            }

            Flush(batch);
        }
        catch (Exception ex)
        {
            // Capture must never take the proxy down; records after this point are lost
            // and that is loudly logged, but traffic keeps flowing.
            logger.LogError(ex, "capture writer failed; further requests will not be recorded");
        }
    }

    private void Flush(List<CaptureRecord> batch)
    {
        if (batch.Count == 0)
        {
            return;
        }

        store.InsertBatch(batch);
        store.EnforceRetention();
    }
}
