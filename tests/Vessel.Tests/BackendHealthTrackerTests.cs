using Vessel.Api;
using Vessel.Capture;
using Vessel.Config;
using Vessel.Formats;
using Vessel.Storage;
using Xunit;

namespace Vessel.Tests;

public sealed class BackendHealthTrackerTests
{
    [Theory]
    [InlineData(null, true, false)]
    [InlineData("Request", true, true)]
    [InlineData("RequestTimedOut", true, true)]
    [InlineData("upstream_unreachable", true, true)]
    [InlineData("upstream_timeout", true, true)]
    [InlineData("ResponseBodyDestination", false, false)]
    [InlineData("UpgradeActivityTimeout", false, false)]
    [InlineData("client_disconnect", false, false)]
    public void Classification_OnlyTreatsConnectClassFailuresAsUnavailable(
        string? error, bool isOutcome, bool unavailable)
    {
        Assert.Equal(isOutcome, BackendHealthTracker.IsHealthOutcome(error));
        Assert.Equal(unavailable, BackendHealthTracker.IsUnavailable(error));
    }

    [Fact]
    public void Seed_UsesLatestPersistedOutcomePerBackend()
    {
        string tempDir = Directory.CreateTempSubdirectory("vessel-health-tests-").FullName;
        string dbPath = Path.Combine(tempDir, "vessel.db");
        try
        {
            using var store = new SqliteCaptureStore(dbPath, new VesselConfig());
            store.Initialize();
            store.EnsureInitialSession();
            var enricher = new FormatEnricher(new VesselConfig());

            CaptureRecord earlierSuccess = TestCapture.Record("/success") with
            {
                Backend = "alpha",
                StartedAt = "2026-08-27T14:31:00.0000000Z",
            };
            CaptureRecord latestFailure = TestCapture.Record("/failure", error: VesselErrors.UpstreamTimeout) with
            {
                Backend = "alpha",
                StartedAt = "2026-08-27T14:32:00.0000000Z",
            };
            CaptureRecord backendError = TestCapture.Record("/backend-error", status: 500) with
            {
                Backend = "beta",
                StartedAt = "2026-08-27T14:33:00.0000000Z",
            };
            CaptureRecord clientDisconnect = TestCapture.Record("/client-disconnect", error: VesselErrors.ClientDisconnect) with
            {
                Backend = "alpha",
                StartedAt = "2026-08-27T14:34:00.0000000Z",
            };
            store.InsertBatch([
                enricher.Enrich(earlierSuccess), enricher.Enrich(latestFailure),
                enricher.Enrich(backendError), enricher.Enrich(clientDisconnect),
            ]);

            var tracker = new BackendHealthTracker(new SqliteReadStore(dbPath));
            tracker.Seed();

            Assert.Equal(BackendHealthTracker.Red, tracker.Get("alpha").State);
            Assert.Equal("2026-08-27T14:32:00.0000000Z", tracker.Get("alpha").LastSeenAt);
            Assert.Equal(BackendHealthTracker.Green, tracker.Get("beta").State);
            Assert.Equal(BackendHealthTracker.Unknown, tracker.Get("not-observed").State);
            Assert.Null(tracker.Get("not-observed").LastSeenAt);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
