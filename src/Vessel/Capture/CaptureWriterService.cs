using System.Text.Json;
using System.Threading.Channels;
using Vessel.Formats;
using Vessel.Storage;

namespace Vessel.Capture;

/// <summary>
/// The background writer (§6.1): initializes the database and the current session (D4)
/// before Kestrel starts accepting traffic (registered ahead of the server's hosted
/// service, so a broken DB fails startup fast), then consumes the channel in batches —
/// one transaction per batch, flushed at 64 records or 250 ms after the batch's first
/// record, whichever comes first. Each captured record is enriched (format detection +
/// adapters) here, off the request path (D1); each session-reset command runs its insert
/// here too, on this single writer thread (D4). Retention runs after each capture batch.
/// The <c>completed</c> SSE event (D5) is emitted per row right after insert, carrying the
/// real DB id and enriched fields — or <c>row: null</c> when the batch is dropped.
/// Shutdown completes the channel and drains what's left.
/// <para>
/// Resilience (phase-1 carry-in): a single failing batch — a transient <c>SQLITE_BUSY</c>
/// from a DB browser holding a write lock, a momentary disk-full — is logged and dropped,
/// and the loop continues. Only after <see cref="MaxConsecutiveFailures"/> consecutive
/// failures does the writer give up loudly.
/// </para>
/// </summary>
public sealed class CaptureWriterService(
    CaptureChannel channel, ICaptureStore store, FormatEnricher enricher,
    CaptureEvents events, CurrentSession currentSession,
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
        currentSession.Set(store.EnsureInitialSession().Id);
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
        ChannelReader<CaptureWork> reader = channel.Reader;
        var batch = new List<CaptureWork>(MaxBatchSize);

        while (await reader.WaitToReadAsync())
        {
            batch.Clear();
            Task deadline = Task.Delay(FlushInterval);

            while (batch.Count < MaxBatchSize)
            {
                while (batch.Count < MaxBatchSize && reader.TryRead(out CaptureWork? item))
                {
                    batch.Add(item);
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
        while (reader.TryRead(out CaptureWork? item))
        {
            batch.Add(item);
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
    /// Splits the batch into session commands (run immediately, on this thread) and
    /// captured requests (enriched + inserted together). Returns false only when the
    /// writer has failed <see cref="MaxConsecutiveFailures"/> times in a row on the
    /// capture-insert path and is giving up; a single failure there is logged, the batch
    /// dropped, and true returned so the loop keeps running.
    /// </summary>
    private bool Flush(List<CaptureWork> batch)
    {
        if (batch.Count == 0)
        {
            return true;
        }

        var captures = new List<CaptureRecord>(batch.Count);
        foreach (CaptureWork item in batch)
        {
            switch (item)
            {
                case CapturedRequest request:
                    captures.Add(request.Record);
                    break;
                case CreateSessionCommand command:
                    RunCreateSession(command);
                    break;
            }
        }

        if (captures.Count == 0)
        {
            return true;
        }

        try
        {
            var enriched = new List<EnrichedRecord>(captures.Count);
            foreach (CaptureRecord record in captures)
            {
                enriched.Add(enricher.Enrich(record));
            }

            IReadOnlyList<long> ids = store.InsertBatch(enriched);
            store.EnforceRetention();
            _consecutiveFailures = 0;

            for (int i = 0; i < enriched.Count; i++)
            {
                events.Completed(enriched[i].Record.Seq, ToSummary(ids[i], enriched[i]));
            }

            return true;
        }
        catch (Exception ex)
        {
            _consecutiveFailures++;
            foreach (CaptureRecord record in captures)
            {
                events.Completed(record.Seq, null); // let the UI clear the in-flight entry
            }

            if (_consecutiveFailures >= MaxConsecutiveFailures)
            {
                logger.LogError(
                    ex, "capture writer failed {Failures} times consecutively; giving up — further requests will not be recorded",
                    _consecutiveFailures);
                return false;
            }

            logger.LogWarning(
                ex, "capture batch of {Size} dropped after a write failure ({Failures}/{Max}); continuing",
                captures.Count, _consecutiveFailures, MaxConsecutiveFailures);
            return true;
        }
    }

    /// <summary>D4 — runs a <c>POST /sessions</c> insert on the writer thread; never throws out.</summary>
    private void RunCreateSession(CreateSessionCommand command)
    {
        try
        {
            command.Completion.TrySetResult(store.CreateSession(command.Name));
        }
        catch (Exception ex)
        {
            command.Completion.TrySetException(ex);
        }
    }

    private static Summary ToSummary(long id, EnrichedRecord enriched)
    {
        CaptureRecord record = enriched.Record;
        return new Summary(
            Id: id,
            StartedAt: record.StartedAt,
            SessionId: record.SessionId,
            Backend: record.Backend,
            Tags: ParseStringArray(record.TagsJson),
            Method: record.Method,
            Path: record.Path,
            Format: enriched.Format,
            Model: enriched.Model,
            StatusCode: record.StatusCode,
            Error: record.Error,
            Streamed: record.Streamed,
            ReplayOf: null,
            DurationMs: record.DurationMs,
            TtftMs: record.TtftMs,
            VesselOverheadMs: record.VesselOverheadMs,
            TokPerSec: enriched.TokPerSec,
            TokensIn: enriched.TokensIn,
            TokensOut: enriched.TokensOut,
            TokensCachedRead: enriched.TokensCachedRead,
            TokensCachedWrite: enriched.TokensCachedWrite,
            TokensEstimated: enriched.TokensEstimated,
            StopReason: enriched.StopReason,
            Warnings: ParseStringArray(enriched.WarningsJson),
            Truncated: record.Truncated);
    }

    private static string[] ParseStringArray(string? json) =>
        json is null ? [] : JsonSerializer.Deserialize(json, CaptureJsonContext.Default.StringArray) ?? [];
}
