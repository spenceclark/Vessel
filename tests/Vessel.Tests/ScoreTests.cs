using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Vessel.Config;
using Vessel.Storage;
using Xunit;

namespace Vessel.Tests;

/// <summary>
/// #49 — the score write path (PUT validation, writer round-trip) and the leaderboard
/// projections it feeds (mean, scored count, replay-group win rate, by=patch).
/// </summary>
public sealed class ScoreTests
{
    private static CancellationToken CT => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("{\"score\":0}")]
    [InlineData("{\"score\":6}")]
    [InlineData("{\"score\":2.5}")]
    [InlineData("{\"score\":\"4\"}")]
    [InlineData("{\"score\":true}")]
    [InlineData("{}")]
    [InlineData("")]
    [InlineData("not-json")]
    public async Task Score_RejectsAnythingButAnIntegerOneToFiveOrNull(string body)
    {
        await using TestVessel vessel = await TestVessel.StartAsync();
        using var client = new HttpClient();
        long id = await Capture(client, vessel);

        using HttpResponseMessage response = await Put(client, vessel, id, body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", response.Headers.GetValues("X-Vessel-Error").Single());
        Assert.Null(await ReadScore(client, vessel, id));
    }

    [Fact]
    public async Task Score_SetsThenClears_AndSurvivesOnTheRow()
    {
        await using TestVessel vessel = await TestVessel.StartAsync();
        using var client = new HttpClient();
        long id = await Capture(client, vessel);

        using (HttpResponseMessage set = await Put(client, vessel, id, "{\"score\":4}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, set.StatusCode);
        }
        Assert.Equal(4, await ReadScore(client, vessel, id));

        using (HttpResponseMessage cleared = await Put(client, vessel, id, "{\"score\":null}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, cleared.StatusCode);
        }
        Assert.Null(await ReadScore(client, vessel, id));
    }

    [Fact]
    public async Task Score_UnknownRowIs404()
    {
        await using TestVessel vessel = await TestVessel.StartAsync();
        using var client = new HttpClient();

        using HttpResponseMessage response = await Put(client, vessel, 9999, "{\"score\":3}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("not_found", response.Headers.GetValues("X-Vessel-Error").Single());
    }

    // D3 — `score` extends SummaryColumns, so the preview column that is appended *after*
    // it moves by one. Reading the wrong ordinal there is silent, hence this test.
    [Fact]
    public void ReadStore_PromptPreviewStillReadsCorrectlyBesideScore()
    {
        using var harness = new Harness();
        long id = harness.Seed(model: "m", promptPreview: "the preview text", score: 5);

        Summary row = harness.Read.ListRequests(10, null, null, null, includePreview: true).Rows.Single();
        Assert.Equal(id, row.Id);
        Assert.Equal("the preview text", row.PromptPreview);
        Assert.Equal(5, row.Score);
    }

    [Fact]
    public void Aggregate_ByModel_CarriesMeanScoreAndScoredCount()
    {
        using var harness = new Harness();
        harness.Seed(model: "alpha", score: 4);
        harness.Seed(model: "alpha", score: 2);
        harness.Seed(model: "alpha", score: null);
        harness.Seed(model: "beta", score: null);

        AggregateResponse response = harness.Read.GetAggregate(
            new AggregateQuery(new RequestQuery(), AggregateDimension.Model));

        AggregateRow alpha = response.Rows.Single(row => row.Key == "alpha");
        Assert.Equal(3, alpha.MeanScore);
        Assert.Equal(2, alpha.Scored);

        AggregateRow beta = response.Rows.Single(row => row.Key == "beta");
        Assert.Null(beta.MeanScore);
        Assert.Equal(0, beta.Scored);
    }

    // Ties are wins for everyone at the top: 4/4/5/4 is a finding about the models, not a
    // rounding problem to break.
    [Fact]
    public void Aggregate_WinRate_TiesWinForEveryModelAtTheTop()
    {
        using var harness = new Harness();
        long original = harness.Seed(model: "base", score: null);
        harness.Seed(model: "alpha", score: 5, replayOf: original, replayGroup: "fan1");
        harness.Seed(model: "beta", score: 5, replayOf: original, replayGroup: "fan1");
        harness.Seed(model: "gamma", score: 2, replayOf: original, replayGroup: "fan1");

        AggregateResponse response = harness.Read.GetAggregate(
            new AggregateQuery(new RequestQuery(), AggregateDimension.Model));

        Assert.Equal((1L, 1L), Wins(response, "alpha"));
        Assert.Equal((1L, 1L), Wins(response, "beta"));
        Assert.Equal((0L, 1L), Wins(response, "gamma"));
        // The original is unscored, so it took part in no comparison at all.
        Assert.Equal((0L, 0L), Wins(response, "base"));
    }

    [Fact]
    public void Aggregate_WinRate_CountsTheOriginalAsAMemberOfItsOwnFan()
    {
        using var harness = new Harness();
        long original = harness.Seed(model: "base", score: 5);
        harness.Seed(model: "alpha", score: 3, replayOf: original, replayGroup: "fan1");
        harness.Seed(model: "beta", score: 4, replayOf: original, replayGroup: "fan1");

        AggregateResponse response = harness.Read.GetAggregate(
            new AggregateQuery(new RequestQuery(), AggregateDimension.Model));

        Assert.Equal((1L, 1L), Wins(response, "base"));
        Assert.Equal((0L, 1L), Wins(response, "alpha"));
        Assert.Equal((0L, 1L), Wins(response, "beta"));
    }

    // A key that fields two members of one fan still wins that fan once, so wins never
    // exceed groups — which is what makes the "top 11/15" label readable.
    [Fact]
    public void Aggregate_WinRate_CountsOneWinPerGroupEvenWithTwoMembersOfTheSameKey()
    {
        using var harness = new Harness();
        long original = harness.Seed(model: "base", score: null);
        harness.Seed(model: "alpha", score: 5, replayOf: original, replayGroup: "fan1");
        harness.Seed(model: "alpha", score: 5, replayOf: original, replayGroup: "fan1");

        AggregateResponse response = harness.Read.GetAggregate(
            new AggregateQuery(new RequestQuery(), AggregateDimension.Model));

        Assert.Equal((1L, 1L), Wins(response, "alpha"));
    }

    [Fact]
    public void Aggregate_ByPatch_ExcludesUnpatchedRowsAndGroupsAcrossOriginals()
    {
        using var harness = new Harness();
        const string cold = """{"temperature":0.2}""";
        const string warm = """{"temperature":0.9}""";
        long first = harness.Seed(model: "m", score: null);
        harness.Seed(model: "m", score: 5, replayOf: first, replayGroup: "fan1", replayPatch: cold);
        harness.Seed(model: "m", score: 3, replayOf: first, replayGroup: "fan1", replayPatch: warm);
        long second = harness.Seed(model: "m", score: null);
        harness.Seed(model: "m", score: 4, replayOf: second, replayGroup: "fan2", replayPatch: cold);
        harness.Seed(model: "m", score: 2, replayOf: second, replayGroup: "fan2", replayPatch: warm);

        AggregateResponse response = harness.Read.GetAggregate(
            new AggregateQuery(new RequestQuery(), AggregateDimension.Patch));

        // Only the patched rows are parameter sets; the two originals are not a group.
        Assert.Equal([cold, warm], response.Rows.Select(row => row.Key).OrderBy(key => key, StringComparer.Ordinal));
        AggregateRow coldRow = response.Rows.Single(row => row.Key == cold);
        Assert.Equal(2, coldRow.Requests);
        Assert.Equal(4.5, coldRow.MeanScore);
        // One patch across two prompts: it won both fans.
        Assert.Equal((2L, 2L), Wins(response, cold));
        Assert.Equal((0L, 2L), Wins(response, warm));
    }

    // Review — the scope selects which fans are in play; it must not change who won one.
    // Filtering to alpha hides beta, and alpha's loss would otherwise read as a win.
    [Fact]
    public void Aggregate_WinRate_IsNotChangedByTheReportsOwnRowFilter()
    {
        using var harness = new Harness();
        long original = harness.Seed(model: "base", score: null);
        harness.Seed(model: "alpha", score: 3, replayOf: original, replayGroup: "fan1");
        harness.Seed(model: "beta", score: 5, replayOf: original, replayGroup: "fan1");

        AggregateResponse unfiltered = harness.Read.GetAggregate(
            new AggregateQuery(new RequestQuery(), AggregateDimension.Model));
        Assert.Equal((0L, 1L), Wins(unfiltered, "alpha"));

        AggregateResponse filtered = harness.Read.GetAggregate(
            new AggregateQuery(new RequestQuery(Model: "alpha"), AggregateDimension.Model));
        Assert.Equal((0L, 1L), Wins(filtered, "alpha"));
    }

    // Review — a fan is selected from either side of the link. Scoping to the original's own
    // model (or its session, since replays land in the current one) must still find the fan
    // it heads, or the original's recorded win disappears.
    [Fact]
    public void Aggregate_WinRate_SelectsFansThroughAMatchingOriginalToo()
    {
        using var harness = new Harness();
        long original = harness.Seed(model: "base", score: 5);
        harness.Seed(model: "alpha", score: 3, replayOf: original, replayGroup: "fan1");
        harness.Seed(model: "beta", score: 4, replayOf: original, replayGroup: "fan1");

        AggregateResponse unfiltered = harness.Read.GetAggregate(
            new AggregateQuery(new RequestQuery(), AggregateDimension.Model));
        Assert.Equal((1L, 1L), Wins(unfiltered, "base"));

        // Only the original matches this filter; its fan is reached through replay_of.
        AggregateResponse filtered = harness.Read.GetAggregate(
            new AggregateQuery(new RequestQuery(Model: "base"), AggregateDimension.Model));
        Assert.Equal((1L, 1L), Wins(filtered, "base"));
    }

    // Review — ranking after the server's group cap is not the scope's leaderboard: a quiet
    // 5/5 model would sit behind every chatty 1/5 one and be truncated away.
    [Fact]
    public void Aggregate_RankByScore_DropsUnscoredGroupsAndOrdersByMean()
    {
        using var harness = new Harness();
        harness.Seed(model: "quiet", score: 5);
        harness.Seed(model: "loud", score: 1);
        harness.Seed(model: "loud", score: 1);
        harness.Seed(model: "unrated", score: null);

        AggregateResponse ranked = harness.Read.GetAggregate(
            new AggregateQuery(new RequestQuery(), AggregateDimension.Model, AggregateRank.Score));

        Assert.Equal(["quiet", "loud"], ranked.Rows.Select(row => row.Key));
        // totalGroups is the ranked population, so the card's "top N of M" stays honest.
        Assert.Equal(2, ranked.TotalGroups);

        AggregateResponse byTokens = harness.Read.GetAggregate(
            new AggregateQuery(new RequestQuery(), AggregateDimension.Model));
        Assert.Equal(3, byTokens.TotalGroups);
    }

    [Fact]
    public void Aggregate_WinsAreNullForDimensionsWithoutFans()
    {
        using var harness = new Harness();
        long original = harness.Seed(model: "m", backend: "alpha", score: 3);
        harness.Seed(model: "m", backend: "alpha", score: 5, replayOf: original, replayGroup: "fan1");

        AggregateResponse response = harness.Read.GetAggregate(
            new AggregateQuery(new RequestQuery(), AggregateDimension.Backend));

        AggregateRow row = response.Rows.Single();
        Assert.Null(row.Wins);
        Assert.Null(row.Groups);
        Assert.Equal(4, row.MeanScore);
    }

    private static (long?, long?) Wins(AggregateResponse response, string key)
    {
        AggregateRow? row = response.Rows.SingleOrDefault(candidate => candidate.Key == key);
        return row is null ? (null, null) : (row.Wins ?? 0, row.Groups ?? 0);
    }

    private static Task<HttpResponseMessage> Put(HttpClient client, TestVessel vessel, long id, string body) =>
        client.PutAsync(
            $"{vessel.BaseUrl}/vessel/api/requests/{id}/score",
            new StringContent(body, Encoding.UTF8, "application/json"),
            CT);

    private static async Task<int?> ReadScore(HttpClient client, TestVessel vessel, long id)
    {
        using HttpResponseMessage response = await client.GetAsync($"{vessel.BaseUrl}/vessel/api/requests/{id}", CT);
        using JsonDocument detail = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CT));
        JsonElement score = detail.RootElement.GetProperty("score");
        return score.ValueKind == JsonValueKind.Null ? null : score.GetInt32();
    }

    private static async Task<long> Capture(HttpClient client, TestVessel vessel)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            $"{vessel.BaseUrl}/api/chat?score-case", new { model = "m", messages = Array.Empty<object>() }, CT);
        CapturedRow row = await CaptureDb.WaitForRow(vessel.DbPath, candidate => candidate.Path.Contains("score-case"));
        return row.Id;
    }

    /// <summary>
    /// Rows are seeded directly, like <see cref="ChartQueryTests"/>' own harness: these
    /// tests need exact score/replay-column combinations, not wire-true payloads.
    /// </summary>
    private sealed class Harness : IDisposable
    {
        private readonly string _dir;

        public Harness()
        {
            _dir = Directory.CreateTempSubdirectory("vessel-score-").FullName;
            DbPath = Path.Combine(_dir, "vessel.db");
            using var writer = new SqliteCaptureStore(DbPath, new VesselConfig());
            writer.Initialize();
            writer.EnsureInitialSession();
        }

        public string DbPath { get; }

        public SqliteReadStore Read => new(DbPath);

        public long Seed(
            string? model = null,
            int? score = null,
            long? replayOf = null,
            string? replayGroup = null,
            string? replayPatch = null,
            string backend = "alpha",
            string? promptPreview = null)
        {
            using var connection = new SqliteConnection($"Data Source={DbPath};Pooling=False");
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO requests
                    (started_at, backend, method, path, format, model, status_code, streamed,
                     replay_of, replay_group, replay_patch, score, prompt_preview, request_headers)
                VALUES
                    ('2026-09-05T09:00:00.000Z', $backend, 'POST', '/api/chat', 'ollama-chat', $model, 200, 0,
                     $replayOf, $replayGroup, $replayPatch, $score, $promptPreview, '{}');
                SELECT last_insert_rowid();
                """;
            command.Parameters.AddWithValue("$backend", backend);
            command.Parameters.AddWithValue("$model", (object?)model ?? DBNull.Value);
            command.Parameters.AddWithValue("$replayOf", (object?)replayOf ?? DBNull.Value);
            command.Parameters.AddWithValue("$replayGroup", (object?)replayGroup ?? DBNull.Value);
            command.Parameters.AddWithValue("$replayPatch", (object?)replayPatch ?? DBNull.Value);
            command.Parameters.AddWithValue("$score", (object?)score ?? DBNull.Value);
            command.Parameters.AddWithValue("$promptPreview", (object?)promptPreview ?? DBNull.Value);
            return (long)command.ExecuteScalar()!;
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(_dir, recursive: true);
            }
            catch (IOException)
            {
                // A temp directory that outlives the test run is not a test failure.
            }
        }
    }
}
