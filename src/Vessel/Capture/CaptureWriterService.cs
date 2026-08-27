using System.Threading.Channels;
using Vessel.Formats;
using Vessel.Storage;

namespace Vessel.Capture;

/// <summary>
/// The background writer (§6.1): initializes the database before Kestrel starts
/// accepting traffic (registered ahead of the server's hosted service, so a broken DB
/// fails startup fast), then consumes the channel in batches — one transaction per
/// batch, flushed at 64 records or 250 ms after the batch's first record, whichever
/// comes first. Each record is enriched (format detection + adapters) here, off the
/// request path (D1). Retention runs after each batch. Shutdown completes the channel and
/// drains what's left.
/// <para>
/// Resilience (phase-1 carry-in): a single failing batch — a transient <c>SQLITE_BUSY</c>
/// from a DB browser holding a write lock, a momentary disk-full — is logged and dropped,
/// and the loop continues. Only after <see cref="MaxConsecutiveFailures"/> consecutive
/// failures does the writer give up loudly.
/// </para>
/// </summary>
public sealed class CaptureWriterService(
    CaptureChannel channel, ICaptureStore store, FormatEnricher enricher,
    ILogger<CaptureWriterService> logger) : IHostedService
{
    public const int MaxBatchSize = 64;

    public const int MaxConsecutiveFailures = 5;

    public static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(250);

    private Task _loop = Task.CompletedTask;

    private int _consecutiveFailures;

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

            if (!Flush(batch))
            {
                return; // gave up loudly
            }
        }

        // Channel completed: drain anything that raced in.
        batch.Clear();
        while (reader.TryRead(out CaptureRecord? record))
        {
            batch.Add(record);
            if (batch.Count >= MaxBatchSize)
            {
                if (!Flush(batch))
                {
                    return;
                }

                batch.Clear();
            }
        }

        Flush(batch);
    }

    /// <summary>
    /// Enriches and writes one batch. Returns false only when the writer has failed
    /// <see cref="MaxConsecutiveFailures"/> times in a row and is giving up; a single
    /// failure is logged, the batch dropped, and true returned so the loop keeps running.
    /// </summary>
    private bool Flush(List<CaptureRecord> batch)
    {
        if (batch.Count == 0)
        {
            return true;
        }

        try
        {
            var enriched = new List<EnrichedRecord>(batch.Count);
            foreach (CaptureRecord record in batch)
            {
                enriched.Add(enricher.Enrich(record));
            }

            store.InsertBatch(enriched);
            store.EnforceRetention();
            _consecutiveFailures = 0;
            return true;
        }
        catch (Exception ex)
        {
            _consecutiveFailures++;
            if (_consecutiveFailures >= MaxConsecutiveFailures)
            {
                logger.LogError(
                    ex, "capture writer failed {Failures} times consecutively; giving up — further requests will not be recorded",
                    _consecutiveFailures);
                return false;
            }

            logger.LogWarning(
                ex, "capture batch of {Size} dropped after a write failure ({Failures}/{Max}); continuing",
                batch.Count, _consecutiveFailures, MaxConsecutiveFailures);
            return true;
        }
    }
}
