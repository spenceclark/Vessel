using Vessel.Config;
using Vessel.Formats;
using Vessel.Storage;
using Xunit;

namespace Vessel.Tests;

/// <summary>
/// F8 — FTS population and consistency (D10): parsed rows are searchable, raw rows never
/// enter FTS, and both retention caps delete FTS rows alongside their <c>requests</c> rows
/// so no orphaned FTS row is ever left behind.
/// </summary>
public class FtsRetentionTests
{
    private static readonly FormatEnricher _enricher = new(new VesselConfig());

    private static EnrichedRecord OllamaRow(string promptPhrase, string responsePhrase, int contentPadding = 0)
    {
        // Padding must be incompressible or zstd shrinks it away and the size cap never trips.
        string content = contentPadding == 0 ? responsePhrase : responsePhrase + IncompressibleText(contentPadding);
        return _enricher.Enrich(TestCapture.Record(
            "/api/chat",
            $$"""{"model":"m","messages":[{"role":"user","content":"{{promptPhrase}}"}]}""",
            $$"""{"model":"m","message":{"role":"assistant","content":"{{content}}"},"done":true,"done_reason":"stop","prompt_eval_count":1,"eval_count":1,"eval_duration":1000000}"""));
    }

    private static string IncompressibleText(int length)
    {
        byte[] random = new byte[length];
        System.Security.Cryptography.RandomNumberGenerator.Fill(random);
        // Base64 keeps it JSON-safe (no quotes/backslashes) while staying near-incompressible.
        return Convert.ToBase64String(random)[..length];
    }

    private static EnrichedRecord RawRow(string marker) =>
        _enricher.Enrich(TestCapture.Record($"/unknown/{marker}", "not json", "not json either"));

    [Fact]
    public void ParsedRowsSearchable_RawRowsAbsent()
    {
        string dir = Directory.CreateTempSubdirectory("vessel-fts-").FullName;
        try
        {
            string dbPath = Path.Combine(dir, "vessel.db");
            using (var store = new SqliteCaptureStore(dbPath, new VesselConfig()))
            {
                store.Initialize();
                store.EnsureInitialSession();
                store.InsertBatch([OllamaRow("pineapple", "watermelon"), RawRow("garbagerow")]);
            }

            Assert.Single(CaptureDb.FtsSearch(dbPath, "pineapple"));   // prompt text
            Assert.Single(CaptureDb.FtsSearch(dbPath, "watermelon"));  // response text
            Assert.Empty(CaptureDb.FtsSearch(dbPath, "garbagerow"));   // raw row never indexed
            Assert.Equal(1, CaptureDb.FtsCount(dbPath));               // only the parsed row
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Retention_MaxRequests_LeavesNoOrphanedFtsRows()
    {
        string dir = Directory.CreateTempSubdirectory("vessel-fts-").FullName;
        try
        {
            string dbPath = Path.Combine(dir, "vessel.db");
            var config = new VesselConfig { Retention = new RetentionConfig { MaxRequests = 5 } };
            using (var store = new SqliteCaptureStore(dbPath, config))
            {
                store.Initialize();
                store.EnsureInitialSession();
                for (int i = 0; i < 12; i++)
                {
                    store.InsertBatch([OllamaRow($"prompt{i}", $"response{i}")]);
                }

                store.EnforceRetention();
            }

            int requestRows = CaptureDb.Query(dbPath).Count;
            Assert.Equal(5, requestRows);
            Assert.Equal(requestRows, CaptureDb.FtsCount(dbPath)); // one FTS row per surviving row, no orphans
            Assert.Empty(CaptureDb.FtsSearch(dbPath, "response0")); // oldest, deleted from FTS too
            Assert.Single(CaptureDb.FtsSearch(dbPath, "response11")); // newest survives
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Retention_MaxDbSize_LeavesNoOrphanedFtsRows()
    {
        string dir = Directory.CreateTempSubdirectory("vessel-fts-").FullName;
        try
        {
            string dbPath = Path.Combine(dir, "vessel.db");
            var config = new VesselConfig { Retention = new RetentionConfig { MaxDbSizeMb = 1 } };
            using (var store = new SqliteCaptureStore(dbPath, config))
            {
                store.Initialize();
                store.EnsureInitialSession();
                // ~250 KB of content per row pushes the file over the 1 MB cap after a few rows.
                for (int i = 0; i < 12; i++)
                {
                    store.InsertBatch([OllamaRow($"prompt{i}", $"response{i}", contentPadding: 250_000)]);
                    store.EnforceRetention();
                }
            }

            int requestRows = CaptureDb.Query(dbPath).Count;
            Assert.True(requestRows < 12, "expected the size cap to have deleted some rows");
            Assert.Equal(requestRows, CaptureDb.FtsCount(dbPath)); // no orphaned FTS rows after size-based deletes
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
