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

        public void Initialize()
        {
        }

        public void InsertBatch(IReadOnlyList<EnrichedRecord> batch)
        {
            lock (_lock)
            {
                Attempts++;
                if (ThrowOnAttempt(Attempts))
                {
                    throw new InvalidOperationException("simulated SQLITE_BUSY");
                }

                InsertedPaths.AddRange(batch.Select(e => e.Record.Path));
            }
        }

        public void EnforceRetention()
        {
        }

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
        new(channel, store, new FormatEnricher(new VesselConfig()), NullLogger<CaptureWriterService>.Instance);

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
}
