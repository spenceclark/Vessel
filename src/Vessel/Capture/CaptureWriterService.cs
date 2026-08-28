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
                await DrainAfterStop(reader);
                return;
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
                    await DrainAfterStop(reader);
                    return;
                }

                batch.Clear();
            }
        }

        if (!Flush(batch))
        {
            await DrainAfterStop(reader);
        }
    }

    /// <summary>
    /// R06 — after give-up, nothing will ever be written again, so the queue must not be
    /// left to grow with a consumer that has gone away. <c>CaptureChannel.Stop</c> already
    /// closed admission; <see cref="Flush"/> already released the failing batch's own
    /// remaining items, so this only drains what races in through the channel afterwards,
    /// giving each a terminal transition (R25): a command is failed, and a discarded capture
    /// reaches <c>completed{row:null}</c> so its seq leaves the active set instead of leaking.
    /// </summary>
    private async Task DrainAfterStop(ChannelReader<CaptureWork> reader)
    {
        string reason = channel.StoppedReason ?? "capture stopped";

        try
        {
            while (await reader.WaitToReadAsync())
            {
                while (reader.TryRead(out CaptureWork? item))
                {
                    TerminateDiscarded(item, reason);
                }
            }
        }
        catch (ChannelClosedException)
        {
            // Completed underneath us — nothing left to release.
        }
    }

    /// <summary>
    /// R25 — the terminal transition for a unit of work the writer discards after give-up: a
    /// command's caller is released with <see cref="CaptureStoppedException"/>; a captured
    /// request's seq is completed with <c>row: null</c>, so the hub's active set (and every
    /// client's in-flight view) stops showing it as forever-running.
    /// </summary>
    private void TerminateDiscarded(CaptureWork item, string reason)
    {
        if (item is CapturedRequest request)
        {
            events.Completed(request.Record.Seq, null);
        }
        else
        {
            CaptureChannel.FailIfCommand(item, reason);
        }
    }

    /// <summary>
    /// Executes the batch <em>in queue order</em>. Captures accumulate; reaching a control
    /// command first inserts everything queued ahead of it, then runs the command. Returns
    /// false only when the writer has failed <see cref="MaxConsecutiveFailures"/> times in a
    /// row on the capture-insert path and is giving up; a single failure there is logged, the
    /// batch dropped, and true returned so the loop keeps running.
    /// <para>
    /// R07: commands used to run before any capture in the same batch was inserted, so a
    /// request captured *before* a clear could be written *after* it — the clear reported
    /// <c>deleted: 0</c> and the row survived. Same hazard for clear-before with an eligible
    /// old capture still in the batch. FIFO here is what makes "clear everything up to now"
    /// mean what the user asked.
    /// </para>
    /// </summary>
    private bool Flush(List<CaptureWork> batch)
    {
        if (batch.Count == 0)
        {
            return true;
        }

        var pending = new List<CaptureRecord>(batch.Count);
        for (int i = 0; i < batch.Count; i++)
        {
            CaptureWork item = batch[i];
            if (item is CapturedRequest request)
            {
                pending.Add(request.Record);
                continue;
            }

            // A command observes every capture queued before it, and none queued after.
            if (!InsertPending(pending))
            {
                // Gave up: `pending` (the captures since the last command) was already
                // completed(null) inside InsertPending, and everything before it was inserted
                // or ran. Every item from this command onward never ran — give each a terminal
                // transition (R25) instead of letting the drain re-touch already-settled items.
                TerminateAfterGiveUp(batch, i);
                return false;
            }

            pending.Clear();

            switch (item)
            {
                case CreateSessionCommand command:
                    RunCreateSession(command);
                    break;
                case ClearCommand command:
                    RunClear(command);
                    break;
            }
        }

        // The trailing captures give up as their own batch; InsertPending already completed
        // them (null) on failure, and there is nothing after them to release.
        return InsertPending(pending);
    }

    /// <summary>
    /// R25/R06 — on give-up mid-batch, release every item at or after <paramref name="from"/>
    /// that never ran: a captured request reaches <c>completed{row:null}</c> (its seq leaves
    /// the active set), a command's caller is failed. Items before <paramref name="from"/> were
    /// already settled — inserted, run, or completed(null) by the failing InsertPending — so
    /// they are deliberately not touched here (re-completing a stored row would wrongly flag it
    /// as dropped).
    /// </summary>
    private void TerminateAfterGiveUp(List<CaptureWork> batch, int from)
    {
        string reason = channel.StoppedReason ?? "capture stopped";
        for (int i = from; i < batch.Count; i++)
        {
            TerminateDiscarded(batch[i], reason);
        }
    }

    /// <summary>
    /// Enriches and inserts one run of captures. Returns false only on give-up, after
    /// putting the channel into its terminal state so admission stops immediately (R06).
    /// </summary>
    private bool InsertPending(List<CaptureRecord> captures)
    {
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

                // Close admission before returning: from here on, captures are dropped at the
                // door and commands fail fast instead of awaiting a consumer that has stopped.
                channel.Stop(
                    $"capture stopped after {_consecutiveFailures} consecutive write failures ({ex.GetType().Name}: {ex.Message}). " +
                    "Traffic is still proxied; restart Vessel to resume recording.");
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

    /// <summary>D6 — runs a <c>DELETE /requests</c> clear on the writer thread; never throws out.</summary>
    private void RunClear(ClearCommand command)
    {
        try
        {
            ClearResult result = store.Clear(command.BeforeIso);

            // R23/H0a — publish the in-band `cleared` event at clear-commit time, on the writer
            // thread, so its SSE id orders after every `completed` this clear could have
            // deleted (those rows were inserted, and their `completed` published, earlier in
            // this same FIFO stream). That ordering is what lets the client purge exactly the
            // cleared rows and treat later completions as post-clear.
            //
            // I0a — the same call records the clear as versioned server state, so a client that
            // never receives this frame (a dropped queue, a reconnect) learns it from
            // `GET /active` instead. The frame is the fast path, not the contract.
            events.Cleared(command.BeforeIso, result.BoundaryId);
            command.Completion.TrySetResult(result.Deleted);
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
