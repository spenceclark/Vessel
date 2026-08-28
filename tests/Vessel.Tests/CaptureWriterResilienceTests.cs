using Microsoft.Extensions.Logging.Abstractions;
using Vessel.Capture;
using Vessel.Config;
using Vessel.Formats;
using Vessel.Storage;
using Xunit;

namespace Vessel.Tests;

/// <summary>
/// F10 — writer resilience: a single failing batch is dropped and the loop keeps running;
/// only <see cref="CaptureWriterService.MaxConsecutiveFailures"/> failures in a row make
/// the writer give up.
/// </summary>
public class CaptureWriterResilienceTests
{
    private sealed class FakeStore : ICaptureStore
    {
        private readonly object _lock = new();

        public int Attempts { get; private set; }

        public List<string> InsertedPaths { get; } = [];

        public Func<int, bool> ThrowOnAttempt { get; init; } = _ => false;

        private long _nextId = 1;

        private long _nextSessionId = 1;

        public void Initialize()
        {
        }

        public IReadOnlyList<long> InsertBatch(IReadOnlyList<EnrichedRecord> batch)
        {
            lock (_lock)
            {
                Attempts++;
                if (ThrowOnAttempt(Attempts))
                {
                    throw new InvalidOperationException("simulated SQLITE_BUSY");
                }

                var ids = new List<long>(batch.Count);
                foreach (EnrichedRecord e in batch)
                {
                    InsertedPaths.Add(e.Record.Path);
                    ids.Add(_nextId++);
                }

                return ids;
            }
        }

        public void EnforceRetention()
        {
        }

        public SessionInfo EnsureInitialSession() => new(1, "2026-01-01T00:00:00.0000000Z", "session 1");

        public SessionInfo CreateSession(string? name) =>
            new(Interlocked.Increment(ref _nextSessionId), "2026-01-01T00:00:00.0000000Z", name);

        public int Clear(string? beforeIso) => 0;

        public int SnapshotAttempts()
        {
            lock (_lock)
            {
                return Attempts;
            }
        }

        public List<string> SnapshotInserted()
        {
            lock (_lock)
            {
                return [.. InsertedPaths];
            }
        }
    }

    private static CaptureWriterService NewWriter(CaptureChannel channel, ICaptureStore store) =>
        new(channel, store, new FormatEnricher(new VesselConfig()), new CaptureEvents(), new CurrentSession(),
            NullLogger<CaptureWriterService>.Instance);

    private static async Task WaitFor(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("writer did not reach the expected state within 10s");
            }

            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
    }

    // One batch throws → it's dropped; a later batch still lands.
    [Fact]
    public async Task FailingBatchDropped_LaterBatchesLand()
    {
        var channel = new CaptureChannel();
        var store = new FakeStore { ThrowOnAttempt = attempt => attempt == 1 };
        CaptureWriterService writer = NewWriter(channel, store);
        await writer.StartAsync(TestContext.Current.CancellationToken);

        channel.Enqueue(TestCapture.Record("/dropped"));
        await WaitFor(() => store.SnapshotAttempts() >= 1); // first batch attempted and threw

        channel.Enqueue(TestCapture.Record("/landed"));
        await WaitFor(() => store.SnapshotInserted().Contains("/landed"));

        await writer.StopAsync(TestContext.Current.CancellationToken);

        List<string> inserted = store.SnapshotInserted();
        Assert.Contains("/landed", inserted);
        Assert.DoesNotContain("/dropped", inserted); // the failing batch was dropped, not retried
    }

    // Five consecutive failures → the writer gives up and stops processing.
    [Fact]
    public async Task FiveConsecutiveFailures_WriterGivesUp()
    {
        var channel = new CaptureChannel();
        var store = new FakeStore { ThrowOnAttempt = _ => true };
        CaptureWriterService writer = NewWriter(channel, store);
        await writer.StartAsync(TestContext.Current.CancellationToken);

        // Feed one record at a time so each becomes its own failing batch.
        for (int i = 1; i <= CaptureWriterService.MaxConsecutiveFailures; i++)
        {
            channel.Enqueue(TestCapture.Record($"/fail-{i}"));
            int expected = i;
            await WaitFor(() => store.SnapshotAttempts() >= expected);
        }

        // The loop has given up; a further record is never attempted.
        channel.Enqueue(TestCapture.Record("/after-giveup"));
        await Task.Delay(500, TestContext.Current.CancellationToken);

        Assert.Equal(CaptureWriterService.MaxConsecutiveFailures, store.SnapshotAttempts());
        Assert.Empty(store.SnapshotInserted());

        await writer.StopAsync(TestContext.Current.CancellationToken);
    }

    // R06: after give-up the queue must not keep accepting work with no consumer, and the
    // terminal state must be reportable — a log line was previously the only signal.
    [Fact]
    public async Task AfterGiveUp_ChannelStopsAdmitting_AndReportsReason()
    {
        var channel = new CaptureChannel();
        var store = new FakeStore { ThrowOnAttempt = _ => true };
        CaptureWriterService writer = NewWriter(channel, store);
        await writer.StartAsync(TestContext.Current.CancellationToken);

        for (int i = 1; i <= CaptureWriterService.MaxConsecutiveFailures; i++)
        {
            channel.Enqueue(TestCapture.Record($"/fail-{i}"));
            int expected = i;
            await WaitFor(() => store.SnapshotAttempts() >= expected);
        }

        await WaitFor(() => channel.IsStopped);
        Assert.NotNull(channel.StoppedReason);
        Assert.Contains("restart Vessel", channel.StoppedReason);

        await writer.StopAsync(TestContext.Current.CancellationToken);
    }

    // R06: a command queued *after* give-up fails fast instead of awaiting a completion
    // nobody will ever resolve.
    [Fact]
    public async Task CommandQueuedAfterGiveUp_FailsPromptly()
    {
        var channel = new CaptureChannel();
        var store = new FakeStore { ThrowOnAttempt = _ => true };
        CaptureWriterService writer = NewWriter(channel, store);
        await writer.StartAsync(TestContext.Current.CancellationToken);

        for (int i = 1; i <= CaptureWriterService.MaxConsecutiveFailures; i++)
        {
            channel.Enqueue(TestCapture.Record($"/fail-{i}"));
            int expected = i;
            await WaitFor(() => store.SnapshotAttempts() >= expected);
        }

        await WaitFor(() => channel.IsStopped);

        var clear = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.Enqueue(new ClearCommand(null, clear));
        var session = new TaskCompletionSource<SessionInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.Enqueue(new CreateSessionCommand("after", session));

        // "Promptly" is the point: without the fix these never complete at all.
        await Assert.ThrowsAsync<CaptureStoppedException>(
            () => clear.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<CaptureStoppedException>(
            () => session.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        await writer.StopAsync(TestContext.Current.CancellationToken);
    }

    // R06: a command in the queue when the writer gives up is released, rather than leaving
    // its caller hanging on a consumer that has stopped.
    //
    // Failures are counted per *batch*, not per record (the existing give-up test feeds one
    // at a time for exactly that reason), so this walks the counter to one short of the
    // limit and then submits the capture that trips it together with a command behind it.
    // Three interleavings are possible — the command lands in the failing batch, arrives
    // while the drain loop is running, or arrives after admission closed — and all three
    // must produce the same answer for the caller, which is what makes this assertion the
    // whole contract rather than one path through it.
    [Fact]
    public async Task CommandQueuedAroundGiveUp_IsReleased()
    {
        var channel = new CaptureChannel();
        var store = new FakeStore { ThrowOnAttempt = _ => true };
        CaptureWriterService writer = NewWriter(channel, store);
        await writer.StartAsync(TestContext.Current.CancellationToken);

        for (int i = 1; i < CaptureWriterService.MaxConsecutiveFailures; i++)
        {
            channel.Enqueue(TestCapture.Record($"/fail-{i}"));
            int expected = i;
            await WaitFor(() => store.SnapshotAttempts() >= expected);
        }

        Assert.False(channel.IsStopped); // one failure short

        var clear = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.Enqueue(TestCapture.Record("/fail-final"));
        channel.Enqueue(new ClearCommand(null, clear));

        await Assert.ThrowsAsync<CaptureStoppedException>(
            () => clear.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.True(channel.IsStopped);

        await writer.StopAsync(TestContext.Current.CancellationToken);
    }

    // R07: a capture queued before a clear must be inserted before the clear runs. The probe
    // that found this enqueued /before-clear then a ClearCommand with the writer stopped;
    // the clear reported deleted:0 and the row survived it.
    [Fact]
    public async Task ClearRunsAfterCapturesQueuedBeforeIt()
    {
        var channel = new CaptureChannel();
        var store = new OrderRecordingStore();
        CaptureWriterService writer = NewWriter(channel, store);

        // One batch, in this order: capture, clear, capture.
        channel.Enqueue(TestCapture.Record("/before-clear"));
        var clear = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.Enqueue(new ClearCommand(null, clear));
        channel.Enqueue(TestCapture.Record("/after-clear"));

        await writer.StartAsync(TestContext.Current.CancellationToken);

        int deleted = await clear.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await WaitFor(() => store.Operations.Contains("insert:/after-clear"));
        await writer.StopAsync(TestContext.Current.CancellationToken);

        // Exactly the earlier capture was visible to the clear, and the later one was not.
        Assert.Equal(1, deleted);
        Assert.Equal(
            ["insert:/before-clear", "clear", "insert:/after-clear"],
            store.Operations);
    }

    /// <summary>Records the order of inserts and clears, and makes Clear delete what was inserted so far.</summary>
    private sealed class OrderRecordingStore : ICaptureStore
    {
        private readonly object _lock = new();
        private readonly List<string> _operations = [];
        private readonly List<string> _live = [];
        private long _nextId = 1;
        private long _nextSessionId = 1;

        public List<string> Operations
        {
            get { lock (_lock) { return [.. _operations]; } }
        }

        public void Initialize()
        {
        }

        public IReadOnlyList<long> InsertBatch(IReadOnlyList<EnrichedRecord> batch)
        {
            lock (_lock)
            {
                var ids = new List<long>(batch.Count);
                foreach (EnrichedRecord e in batch)
                {
                    _operations.Add($"insert:{e.Record.Path}");
                    _live.Add(e.Record.Path);
                    ids.Add(_nextId++);
                }

                return ids;
            }
        }

        public void EnforceRetention()
        {
        }

        public SessionInfo EnsureInitialSession() => new(1, "2026-01-01T00:00:00.0000000Z", "session 1");

        public SessionInfo CreateSession(string? name) =>
            new(Interlocked.Increment(ref _nextSessionId), "2026-01-01T00:00:00.0000000Z", name);

        public int Clear(string? beforeIso)
        {
            lock (_lock)
            {
                _operations.Add("clear");
                int deleted = _live.Count;
                _live.Clear();
                return deleted;
            }
        }
    }
}
