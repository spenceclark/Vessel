using Microsoft.Data.Sqlite;
using Vessel.Config;
using Vessel.Storage;
using Xunit;

namespace Vessel.Tests;

/// <summary>
/// Phase 7 D1–D3 — the chart read queries (<see cref="SqliteReadStore.GetSeries"/> /
/// <see cref="SqliteReadStore.GetAggregate"/>) against a fresh store with directly-seeded
/// rows. Direct seeding bypasses the writer deliberately (same reasoning as
/// <see cref="CaptureDb.SeedRow"/>): chart tests need exact tag/model/token/error column
/// combinations that would otherwise require wire-true payloads per case.
/// </summary>
public class ChartQueryTests
{
    // D1 — points come back oldest-first by id (insertion order is the chronology), with
    // the metric value and the ISO started_at carried per point.
    [Fact]
    public void Series_OldestFirstById_CarriesStartedAtAndValue()
    {
        using var harness = new Harness();
        long id1 = harness.Seed(startedAt: "2026-08-31T09:00:00.000Z", path: "/a", tokensIn: 10);
        long id2 = harness.Seed(startedAt: "2026-08-31T09:01:00.000Z", path: "/b", tokensIn: 20);
        long id3 = harness.Seed(startedAt: "2026-08-31T09:02:00.000Z", path: "/c", tokensIn: 30);

        SeriesResponse response = harness.Read.GetSeries(new SeriesQuery(
            new RequestQuery(), SeriesMetric.TokensIn, SeriesGroupBy.None));

        Assert.Single(response.Series);
        Assert.Null(response.Series[0].Key);
        Assert.Equal([id1, id2, id3], response.Series[0].Points.Select(p => p.Id).ToArray());
        Assert.Equal([10L, 20, 30], response.Series[0].Points.Select(p => p.V).ToArray());
        Assert.Equal("2026-08-31T09:00:00.000Z", response.Series[0].Points[0].T);
        Assert.Equal(3, response.Returned);
        Assert.False(response.Truncated);
        Assert.Equal(0, response.TotalMatching); // computed only when truncated
        Assert.Equal(0, response.OmittedSeries);
        Assert.False(response.Estimated);
        Assert.Equal("tokens_in", response.Metric);
        Assert.Equal("none", response.GroupBy);
    }

    // D1 — the session scope is the canonical list predicate's, not a separate rule.
    [Fact]
    public void Series_SessionScope_FollowsListSemantics()
    {
        using var harness = new Harness();
        long otherSession = harness.CreateSession("other-run");
        harness.Seed("2026-08-31T09:00:00.000Z", "/in-scope", sessionId: harness.CurrentSessionId, tokensIn: 1);
        harness.Seed("2026-08-31T09:01:00.000Z", "/out-of-scope", sessionId: otherSession, tokensIn: 2);
        harness.Seed("2026-08-31T09:02:00.000Z", "/no-session", sessionId: null, tokensIn: 4);

        SeriesResponse scoped = harness.Read.GetSeries(new SeriesQuery(
            new RequestQuery(SessionId: harness.CurrentSessionId), SeriesMetric.TokensIn, SeriesGroupBy.None));
        Assert.Equal([1L], scoped.Series[0].Points.Select(p => p.V).ToArray());

        SeriesResponse all = harness.Read.GetSeries(new SeriesQuery(
            new RequestQuery(), SeriesMetric.TokensIn, SeriesGroupBy.None));
        Assert.Equal(3, all.Returned);
    }

    // D1 — null-metric rows are excluded by predicate, so a raw capture with no token
    // counts is not a silent gap in the chart.
    [Fact]
    public void Series_NullMetricRowsExcludedFromPoints()
    {
        using var harness = new Harness();
        harness.Seed("2026-08-31T09:00:00.000Z", "/with-tokens", tokensIn: 10);
        harness.Seed("2026-08-31T09:01:00.000Z", "/raw-no-tokens", tokensIn: null);

        SeriesResponse response = harness.Read.GetSeries(new SeriesQuery(
            new RequestQuery(), SeriesMetric.TokensIn, SeriesGroupBy.None));

        Assert.Single(response.Series[0].Points);
        Assert.Equal(10L, response.Series[0].Points[0].V);
    }

    // D1 — tokens_total sums what a row carries: either-count rows contribute, both-null
    // rows are excluded by the predicate.
    [Fact]
    public void Series_TokensOutAndTotal_ReachEveryCarriedCount()
    {
        using var harness = new Harness();
        harness.Seed("2026-08-31T09:00:00.000Z", "/both", tokensIn: 10, tokensOut: 2);
        harness.Seed("2026-08-31T09:01:00.000Z", "/in-only", tokensIn: 5, tokensOut: null);
        harness.Seed("2026-08-31T09:02:00.000Z", "/out-only", tokensIn: null, tokensOut: 7);

        SeriesResponse outs = harness.Read.GetSeries(new SeriesQuery(
            new RequestQuery(), SeriesMetric.TokensOut, SeriesGroupBy.None));
        Assert.Equal([2L, 7], outs.Series[0].Points.Select(p => p.V).ToArray());
        Assert.Equal(2, outs.Returned);

        SeriesResponse total = harness.Read.GetSeries(new SeriesQuery(
            new RequestQuery(), SeriesMetric.TokensTotal, SeriesGroupBy.None));
        Assert.Equal([12L, 5, 7], total.Series[0].Points.Select(p => p.V).ToArray());
        Assert.Equal(3, total.Returned);
    }

    // D1 — groupBy=tag fans out through json_each: a multi-tag request contributes a point
    // to both series, and an untagged request lands in the null-key ("(none)") series.
    [Fact]
    public void Series_GroupByTag_FansOutMultiTag_UntaggedIsNullKey()
    {
        using var harness = new Harness();
        harness.Seed("2026-08-31T09:00:00.000Z", "/multi", tags: "[\"a\",\"b\"]", tokensIn: 10);
        harness.Seed("2026-08-31T09:01:00.000Z", "/single", tags: "[\"a\"]", tokensIn: 20);
        harness.Seed("2026-08-31T09:02:00.000Z", "/untagged", tags: null, tokensIn: 40);

        SeriesResponse response = harness.Read.GetSeries(new SeriesQuery(
            new RequestQuery(), SeriesMetric.TokensIn, SeriesGroupBy.Tag));

        // Ranked by total metric value desc: (none)=40, a=30, b=10.
        Assert.Equal(3, response.Series.Length);
        Assert.Null(response.Series[0].Key);
        Assert.Equal([40L], response.Series[0].Points.Select(p => p.V).ToArray());
        Assert.Equal("a", response.Series[1].Key);
        Assert.Equal([10L, 20], response.Series[1].Points.Select(p => p.V).ToArray());
        Assert.Equal("b", response.Series[2].Key);
        Assert.Equal([10L], response.Series[2].Points.Select(p => p.V).ToArray());
        Assert.Equal(0, response.OmittedSeries);
    }

    // D1 — the six-series ramp cap: past it, series are ranked and dropped, never merged
    // into an "(other)" line; omittedSeries reports how many were dropped.
    [Fact]
    public void Series_SeriesCap_RanksDropsWithoutMerging()
    {
        using var harness = new Harness();
        long[] totals = [100, 200, 300, 400, 500, 650, 650, 700];
        for (int i = 0; i < totals.Length; i++)
        {
            harness.Seed(
                $"2026-08-31T09:0{i}:00.000Z", $"/t{i + 1}", tags: $"[\"t{i + 1}\"]", tokensIn: totals[i]);
        }

        SeriesResponse response = harness.Read.GetSeries(new SeriesQuery(
            new RequestQuery(), SeriesMetric.TokensIn, SeriesGroupBy.Tag));

        Assert.Equal(6, response.Series.Length);
        // 700 first; the 650 tie breaks by key ascending (t6 before t7); 200 and 100 dropped.
        Assert.Equal(["t8", "t6", "t7", "t5", "t4", "t3"], response.Series.Select(s => s.Key).ToArray());
        Assert.Equal(2, response.OmittedSeries);
        Assert.DoesNotContain("(other)", response.Series.Select(s => s.Key));
    }

    // D1 — when the cap is hit, the newest MaxPoints are returned and totalMatching (the
    // same predicate minus null-metric rows) is computed so the UI can state both numbers.
    [Fact]
    public void Series_Truncation_NewestPoints_CountExcludesNullMetric()
    {
        using var harness = new Harness();
        harness.SeedMany(connection =>
        {
            using var transaction = connection.BeginTransaction();
            for (int i = 0; i < ChartLimits.MaxPoints + 1; i++)
            {
                Harness.Insert(
                    connection, $"2026-08-31T09:00:{(i / 60 % 60):00}.{i % 60:000}Z", $"/bulk-{i}",
                    tokensIn: 1, transaction: transaction);
            }

            Harness.Insert(connection, "2026-08-31T09:59:00.000Z", "/raw-null-metric", tokensIn: null, transaction: transaction);
            transaction.Commit();
        });

        SeriesResponse response = harness.Read.GetSeries(new SeriesQuery(
            new RequestQuery(), SeriesMetric.TokensIn, SeriesGroupBy.None));

        Assert.True(response.Truncated);
        Assert.Equal(ChartLimits.MaxPoints, response.Returned);
        Assert.Equal(ChartLimits.MaxPoints, response.Series[0].Points.Length);
        // Matching valued rows have ids 1..5001 (the null-metric row is 5002): the newest
        // 5000 of those are ids 2..5001, returned oldest-first.
        Assert.Equal(2, response.Series[0].Points[0].Id);
        Assert.Equal(ChartLimits.MaxPoints + 1, response.Series[0].Points[^1].Id);
        Assert.Equal(ChartLimits.MaxPoints + 1, response.TotalMatching);
    }

    // D1/D3 — the DENSE_RANK cap must count *distinct requests*, not fanned-out rows: a
    // multi-tag request contributing two rows must not burn two slots of the newest-N
    // window, or the window (and the disclosed "returned" count) would silently cover
    // fewer real requests than the response states.
    [Fact]
    public void Series_GroupByTag_Truncation_CapsDistinctRequests_NotFannedOutRows()
    {
        using var harness = new Harness();
        harness.SeedMany(connection =>
        {
            using var transaction = connection.BeginTransaction();
            for (int i = 0; i < ChartLimits.MaxPoints + 5; i++)
            {
                Harness.Insert(
                    connection, $"2026-08-31T09:00:{(i / 60 % 60):00}.{i % 60:000}Z", $"/multi-{i}",
                    tags: "[\"a\",\"b\"]", tokensIn: 1, transaction: transaction);
            }

            transaction.Commit();
        });

        SeriesResponse response = harness.Read.GetSeries(new SeriesQuery(
            new RequestQuery(), SeriesMetric.TokensIn, SeriesGroupBy.Tag));

        Assert.True(response.Truncated);
        // Every one of the newest 5000 *requests* is drawn, even though each contributes
        // two fanned-out rows (one per tag) — a row-counted cap would have kept only half.
        Assert.Equal(ChartLimits.MaxPoints, response.Returned);
        Assert.Equal(2, response.Series.Length);
        Assert.All(response.Series, s => Assert.Equal(ChartLimits.MaxPoints, s.Points.Length));
        // Requests are seeded with sequential ids 1..(MaxPoints+5); the newest MaxPoints
        // are ids 6..(MaxPoints+5), oldest-first.
        Assert.Equal(6, response.Series[0].Points[0].Id);
        Assert.Equal(ChartLimits.MaxPoints + 5, response.Series[0].Points[^1].Id);
        Assert.Equal(ChartLimits.MaxPoints + 5, response.TotalMatching);
    }

    // D1/D3 — the three-way combination (q + tag + groupBy=tag) is the one that breaks if
    // the FROM clause assembles the json_each fan-out before the tables its argument
    // references; pinned per the spec.
    [Fact]
    public void Series_ThreeWay_q_tag_groupByTag_Composes()
    {
        using var harness = new Harness();
        harness.Seed("2026-08-31T09:00:00.000Z", "/match", tags: "[\"planner\"]", tokensIn: 5, promptText: "growth needle here");
        harness.Seed("2026-08-31T09:01:00.000Z", "/wrong-text", tags: "[\"planner\"]", tokensIn: 7, promptText: "unrelated words");
        harness.Seed("2026-08-31T09:02:00.000Z", "/wrong-tag", tags: "[\"other\"]", tokensIn: 9, promptText: "growth needle here");

        SeriesResponse response = harness.Read.GetSeries(new SeriesQuery(
            new RequestQuery(Q: "growth needle", Tag: "planner"), SeriesMetric.TokensIn, SeriesGroupBy.Tag));

        Assert.Single(response.Series);
        Assert.Equal("planner", response.Series[0].Key);
        Assert.Single(response.Series[0].Points);
        Assert.Equal(5L, response.Series[0].Points[0].V);
    }

    // D1 — a null grouping column (model) is a null-key series, rendered "(none)".
    [Fact]
    public void Series_GroupByModel_NullModelIsNullKey_RankedByTotal()
    {
        using var harness = new Harness();
        harness.Seed("2026-08-31T09:00:00.000Z", "/no-model", model: null, tokensIn: 40);
        harness.Seed("2026-08-31T09:01:00.000Z", "/model-a", model: "m1", tokensIn: 20);

        SeriesResponse response = harness.Read.GetSeries(new SeriesQuery(
            new RequestQuery(), SeriesMetric.TokensIn, SeriesGroupBy.Model));

        Assert.Equal([null, "m1"], response.Series.Select(s => s.Key).ToArray());
        Assert.Equal([40L, 20], response.Series.Select(s => s.Points.Sum(p => p.V)).ToArray());
    }

    // D1 — estimated describes the drawn points: any contributing row estimated → true.
    [Fact]
    public void Series_Estimated_WhenAnyDrawnRowEstimated()
    {
        using var harness = new Harness();
        harness.Seed("2026-08-31T09:00:00.000Z", "/exact", tokensIn: 10);
        harness.Seed("2026-08-31T09:01:00.000Z", "/estimated", tokensIn: 20, estimated: true);

        SeriesResponse response = harness.Read.GetSeries(new SeriesQuery(
            new RequestQuery(), SeriesMetric.TokensIn, SeriesGroupBy.None));
        Assert.True(response.Estimated);

        // An estimated row outside the drawn window does not flag the chart.
        using var trimmed = new Harness();
        trimmed.Seed("2026-08-31T09:00:00.000Z", "/exact", tokensIn: 10);
        SeriesResponse exact = trimmed.Read.GetSeries(new SeriesQuery(
            new RequestQuery(), SeriesMetric.TokensIn, SeriesGroupBy.None));
        Assert.False(exact.Estimated);
    }

    // D1 — an empty scope draws nothing: no series, no points, no truncation.
    [Fact]
    public void Series_EmptyScope_NoSeries()
    {
        using var harness = new Harness();
        SeriesResponse response = harness.Read.GetSeries(new SeriesQuery(
            new RequestQuery(), SeriesMetric.TokensIn, SeriesGroupBy.Tag));

        Assert.Empty(response.Series);
        Assert.Equal(0, response.Returned);
        Assert.False(response.Truncated);
        Assert.False(response.Estimated);
    }

    // D2 — sorted by tokens in+out desc, then requests desc, then key asc; sums, cached
    // tokens, and the per-group MAX(tokens_estimated) land exactly.
    [Fact]
    public void Aggregate_ByModel_SortOrder_TotalsAndEstimated()
    {
        using var harness = new Harness();
        harness.Seed("2026-08-31T09:00:00.000Z", "/m1", model: "m1", tokensIn: 500, tokensOut: 50, cachedRead: 7, cachedWrite: 3);
        harness.Seed("2026-08-31T09:01:00.000Z", "/m2", model: "m2", tokensIn: 1000, tokensOut: 100, estimated: true);
        harness.Seed("2026-08-31T09:02:00.000Z", "/m3", model: "m3", tokensIn: 200, tokensOut: 900);

        AggregateResponse response = harness.Read.GetAggregate(new AggregateQuery(
            new RequestQuery(), AggregateDimension.Model));

        Assert.Equal("model", response.By);
        Assert.Equal(3, response.TotalGroups);
        // m2 and m3 tie at 1100 tokens; the key tiebreak is ascending → m2, m3, then m1.
        Assert.Equal(["m2", "m3", "m1"], response.Rows.Select(r => r.Key).ToArray());

        AggregateRow m1 = response.Rows.Single(r => r.Key == "m1");
        Assert.Equal(1, m1.Requests);
        Assert.Equal(0, m1.Failed);
        Assert.Equal(550, m1.TokensIn + m1.TokensOut);
        Assert.Equal(7, m1.TokensCachedRead);
        Assert.Equal(3, m1.TokensCachedWrite);
        Assert.False(m1.TokensEstimated);

        Assert.True(response.Rows.Single(r => r.Key == "m2").TokensEstimated);
    }

    // D2 — failed uses the stats predicate verbatim (error set OR status >= 400); avgTtft
    // averages streamed rows only; averages ignore nulls.
    [Fact]
    public void Aggregate_FailedPredicate_AveragesIgnoreNulls_TtftStreamedOnly()
    {
        using var harness = new Harness();
        harness.Seed("2026-08-31T09:00:00.000Z", "/ok", statusCode: 200, durationMs: 100, streamed: true, ttftMs: 100, tokPerSec: 40, tokensIn: 10);
        harness.Seed("2026-08-31T09:01:00.000Z", "/http-500", statusCode: 500, durationMs: 200);
        harness.Seed("2026-08-31T09:02:00.000Z", "/proxy-error", statusCode: null, error: "upstream_unreachable", durationMs: null);
        harness.Seed("2026-08-31T09:03:00.000Z", "/error-but-200", statusCode: 200, error: "client_disconnect");
        // A non-streamed row with a ttft value must never enter the ttft average.
        harness.Seed("2026-08-31T09:04:00.000Z", "/non-streamed-ttft", statusCode: 200, durationMs: 300, streamed: false, ttftMs: 9999);

        AggregateResponse response = harness.Read.GetAggregate(new AggregateQuery(
            new RequestQuery(), AggregateDimension.Backend));

        Assert.Single(response.Rows);
        AggregateRow row = response.Rows[0];
        Assert.Equal(5, row.Requests);
        Assert.Equal(3, row.Failed);
        Assert.Equal(200.0, row.AvgDurationMs); // (100+200+300)/3 — the null duration is ignored
        Assert.Equal(100.0, row.AvgTtftMs); // streamed rows only; the 9999 non-streamed row is ignored
        Assert.Equal(40.0, row.AvgTokPerSec);
        Assert.Equal(10, row.TokensIn);
        Assert.Equal(0, row.TokensOut); // COALESCE sum, never null
    }

    // D2 — by=tag counts a multi-tag request once per tag; the rows can sum past the
    // session total by design (disclosed in the UI).
    [Fact]
    public void Aggregate_ByTag_MultiTagCountedPerTag_UntaggedIsNullKey()
    {
        using var harness = new Harness();
        harness.Seed("2026-08-31T09:00:00.000Z", "/multi", tags: "[\"a\",\"b\"]", tokensIn: 10);
        harness.Seed("2026-08-31T09:01:00.000Z", "/single", tags: "[\"a\"]", tokensIn: 20);
        harness.Seed("2026-08-31T09:02:00.000Z", "/untagged", tags: null, tokensIn: 40);

        AggregateResponse response = harness.Read.GetAggregate(new AggregateQuery(
            new RequestQuery(), AggregateDimension.Tag));

        Assert.Equal(3, response.TotalGroups);
        Assert.Equal([null, "a", "b"], response.Rows.Select(r => r.Key).ToArray()); // 40, 30, 10 by total tokens

        AggregateRow a = response.Rows.Single(r => r.Key == "a");
        Assert.Equal(2, a.Requests); // r1 + r2 — the multi-tag request counts in both groups
        Assert.Equal(30, a.TokensIn);
        Assert.Equal(1, response.Rows.Single(r => r.Key == "b").Requests);
        Assert.Equal(1, response.Rows.Single(r => r.Key is null).Requests);
    }

    // D2 — the 50-group cap with totalGroups reported, and deliberately no "(other)"
    // rollup row.
    [Fact]
    public void Aggregate_GroupCap_ReportsTotalGroups_NoOtherRollup()
    {
        using var harness = new Harness();
        harness.SeedMany(connection =>
        {
            using var transaction = connection.BeginTransaction();
            for (int i = 0; i < ChartLimits.MaxGroups + 5; i++)
            {
                Harness.Insert(
                    connection, $"2026-08-31T09:00:{(i / 60 % 60):00}.{i % 60:000}Z", $"/m{i:00}",
                    model: $"m{i:00}", tokensIn: 1, transaction: transaction);
            }

            transaction.Commit();
        });

        AggregateResponse response = harness.Read.GetAggregate(new AggregateQuery(
            new RequestQuery(), AggregateDimension.Model));

        Assert.Equal(ChartLimits.MaxGroups, response.Rows.Length);
        Assert.Equal(ChartLimits.MaxGroups + 5, response.TotalGroups);
        // Every total ties, so keys order ascending: m00..m49 survive the cap.
        Assert.Equal("m00", response.Rows[0].Key);
        Assert.Equal($"m{ChartLimits.MaxGroups - 1:00}", response.Rows[^1].Key);
        Assert.DoesNotContain(response.Rows, r => r.Key == "(other)");
    }

    // D2 — format is NOT NULL, so by=format never yields a null key (model and tag can).
    [Fact]
    public void Aggregate_ByFormat_NeverNullKey()
    {
        using var harness = new Harness();
        harness.Seed("2026-08-31T09:00:00.000Z", "/chat", format: "ollama-chat", tokensIn: 1);
        harness.Seed("2026-08-31T09:01:00.000Z", "/raw", format: "raw");

        AggregateResponse response = harness.Read.GetAggregate(new AggregateQuery(
            new RequestQuery(), AggregateDimension.Format));

        Assert.Equal(2, response.TotalGroups);
        Assert.All(response.Rows, r => Assert.NotNull(r.Key));
        // "ollama-chat" carries the only token count, so it ranks first by tokens desc.
        Assert.Equal("ollama-chat", response.Rows[0].Key);
        Assert.Equal("raw", response.Rows[1].Key);
    }

    // D2 — the canonical scope applies to aggregates exactly as to the list.
    [Fact]
    public void Aggregate_SessionScope_FollowsListSemantics()
    {
        using var harness = new Harness();
        long otherSession = harness.CreateSession("other-run");
        harness.Seed("2026-08-31T09:00:00.000Z", "/in", sessionId: harness.CurrentSessionId, model: "m1", tokensIn: 1);
        harness.Seed("2026-08-31T09:01:00.000Z", "/out", sessionId: otherSession, model: "m2", tokensIn: 2);

        AggregateResponse scoped = harness.Read.GetAggregate(new AggregateQuery(
            new RequestQuery(SessionId: harness.CurrentSessionId), AggregateDimension.Model));
        Assert.Single(scoped.Rows);
        Assert.Equal("m1", scoped.Rows[0].Key);
        Assert.Equal(1, scoped.TotalGroups);
    }

    [Fact]
    public void Aggregate_EmptyScope_NoRows()
    {
        using var harness = new Harness();
        AggregateResponse response = harness.Read.GetAggregate(new AggregateQuery(
            new RequestQuery(), AggregateDimension.Model));

        Assert.Empty(response.Rows);
        Assert.Equal(0, response.TotalGroups);
    }

    // #26 live-use feedback — nearest-rank p50/p95 per group, computed by a second query
    // (no bundled SQLite PERCENTILE_CONT), excluding null durations from the population.
    [Fact]
    public void Aggregate_Percentiles_NearestRank_ExcludesNullDurations()
    {
        using var harness = new Harness();
        // m1: durations 100,200,300,400 (n=4) — p50 index ceil(.5*4)-1=1 → 200;
        // p95 index ceil(.95*4)-1=3 → 400. A null-duration row must not enter the population.
        harness.Seed("2026-08-31T09:00:00.000Z", "/a", model: "m1", durationMs: 100, tokensIn: 1);
        harness.Seed("2026-08-31T09:01:00.000Z", "/b", model: "m1", durationMs: 200, tokensIn: 1);
        harness.Seed("2026-08-31T09:02:00.000Z", "/c", model: "m1", durationMs: 300, tokensIn: 1);
        harness.Seed("2026-08-31T09:03:00.000Z", "/d", model: "m1", durationMs: 400, tokensIn: 1);
        harness.Seed("2026-08-31T09:04:00.000Z", "/e", model: "m1", durationMs: null, tokensIn: 1);
        // m2: every row has a null duration — percentiles are null, not zero.
        harness.Seed("2026-08-31T09:05:00.000Z", "/f", model: "m2", durationMs: null, tokensIn: 100);

        AggregateResponse response = harness.Read.GetAggregate(new AggregateQuery(
            new RequestQuery(), AggregateDimension.Model));

        AggregateRow m1 = response.Rows.Single(r => r.Key == "m1");
        Assert.Equal(5, m1.Requests); // the null-duration row still counts as a request
        Assert.Equal(200.0, m1.P50DurationMs);
        Assert.Equal(400.0, m1.P95DurationMs);

        AggregateRow m2 = response.Rows.Single(r => r.Key == "m2");
        Assert.Null(m2.P50DurationMs);
        Assert.Null(m2.P95DurationMs);
    }

    // #26 — by=warning fans out through json_each exactly like by=tag: a request flagged
    // with several warning codes is counted once per code, and a clean request is the
    // null-key group.
    [Fact]
    public void Aggregate_ByWarning_FansOutMultiWarning_CleanRequestIsNullKey()
    {
        using var harness = new Harness();
        harness.Seed("2026-08-31T09:00:00.000Z", "/both", warnings: "[\"cold_load\",\"slow_ttft\"]", tokensIn: 10);
        harness.Seed("2026-08-31T09:01:00.000Z", "/one", warnings: "[\"cold_load\"]", tokensIn: 20);
        harness.Seed("2026-08-31T09:02:00.000Z", "/clean", warnings: null, tokensIn: 40);

        AggregateResponse response = harness.Read.GetAggregate(new AggregateQuery(
            new RequestQuery(), AggregateDimension.Warning));

        Assert.Equal("warning", response.By);
        Assert.Equal(3, response.TotalGroups);
        Assert.Equal([null, "cold_load", "slow_ttft"], response.Rows.Select(r => r.Key).ToArray()); // 40, 30, 10 by tokens

        AggregateRow coldLoad = response.Rows.Single(r => r.Key == "cold_load");
        Assert.Equal(2, coldLoad.Requests); // both + one — counted once per warning code
        Assert.Equal(1, response.Rows.Single(r => r.Key == "slow_ttft").Requests);
        Assert.Equal(1, response.Rows.Single(r => r.Key is null).Requests);
    }

    /// <summary>
    /// Store-level harness for the chart queries: a fresh database (the writer store owns
    /// schema + initial session), direct SQL seeding, and the read store under test.
    /// </summary>
    private sealed class Harness : IDisposable
    {
        private readonly string _dir;

        public Harness()
        {
            _dir = Directory.CreateTempSubdirectory("vessel-chart-").FullName;
            DbPath = Path.Combine(_dir, "vessel.db");
            using var writer = new SqliteCaptureStore(DbPath, new VesselConfig());
            writer.Initialize();
            CurrentSessionId = writer.EnsureInitialSession().Id;
        }

        public SqliteReadStore Read => new(DbPath);

        public string DbPath { get; }

        public long CurrentSessionId { get; }

        public long CreateSession(string name)
        {
            using var writer = new SqliteCaptureStore(DbPath, new VesselConfig());
            writer.Initialize();
            return writer.ResolveNamedSession(name).Session.Id;
        }

        public long Seed(
            string startedAt,
            string path,
            long? sessionId = null,
            string backend = "alpha",
            string? tags = null,
            string? model = null,
            string format = "raw",
            int? statusCode = 200,
            string? error = null,
            bool streamed = false,
            double? durationMs = null,
            double? ttftMs = null,
            double? tokPerSec = null,
            long? tokensIn = null,
            long? tokensOut = null,
            long? cachedRead = null,
            long? cachedWrite = null,
            bool estimated = false,
            string? promptText = null,
            string? warnings = null)
        {
            using SqliteConnection connection = OpenWrite();
            return Insert(
                connection, startedAt, path, sessionId, backend, tags, model, format, statusCode,
                error, streamed, durationMs, ttftMs, tokPerSec, tokensIn, tokensOut, cachedRead,
                cachedWrite, estimated, promptText, warnings: warnings);
        }

        /// <summary>Seeds inside one caller-managed connection — the bulk tests wrap this in a transaction.</summary>
        public void SeedMany(Action<SqliteConnection> seed)
        {
            using SqliteConnection connection = OpenWrite();
            seed(connection);
        }

        public static long Insert(
            SqliteConnection connection,
            string startedAt,
            string path,
            long? sessionId = null,
            string backend = "alpha",
            string? tags = null,
            string? model = null,
            string format = "raw",
            int? statusCode = 200,
            string? error = null,
            bool streamed = false,
            double? durationMs = null,
            double? ttftMs = null,
            double? tokPerSec = null,
            long? tokensIn = null,
            long? tokensOut = null,
            long? cachedRead = null,
            long? cachedWrite = null,
            bool estimated = false,
            string? promptText = null,
            SqliteTransaction? transaction = null,
            string? warnings = null)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO requests
                    (started_at, session_id, backend, tags, method, path, format, model, status_code,
                     error, streamed, duration_ms, ttft_ms, tok_per_sec, tokens_in, tokens_out,
                     tokens_cached_read, tokens_cached_write, tokens_estimated, warnings, request_headers)
                VALUES
                    ($startedAt, $sessionId, $backend, $tags, 'GET', $path, $format, $model, $statusCode,
                     $error, $streamed, $durationMs, $ttftMs, $tokPerSec, $tokensIn, $tokensOut,
                     $cachedRead, $cachedWrite, $estimated, $warnings, '{}')
                """;
            command.Parameters.AddWithValue("$startedAt", startedAt);
            command.Parameters.AddWithValue("$sessionId", (object?)sessionId ?? DBNull.Value);
            command.Parameters.AddWithValue("$backend", backend);
            command.Parameters.AddWithValue("$tags", (object?)tags ?? DBNull.Value);
            command.Parameters.AddWithValue("$path", path);
            command.Parameters.AddWithValue("$format", format);
            command.Parameters.AddWithValue("$model", (object?)model ?? DBNull.Value);
            command.Parameters.AddWithValue("$statusCode", (object?)statusCode ?? DBNull.Value);
            command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
            command.Parameters.AddWithValue("$streamed", streamed ? 1 : 0);
            command.Parameters.AddWithValue("$durationMs", (object?)durationMs ?? DBNull.Value);
            command.Parameters.AddWithValue("$ttftMs", (object?)ttftMs ?? DBNull.Value);
            command.Parameters.AddWithValue("$tokPerSec", (object?)tokPerSec ?? DBNull.Value);
            command.Parameters.AddWithValue("$tokensIn", (object?)tokensIn ?? DBNull.Value);
            command.Parameters.AddWithValue("$tokensOut", (object?)tokensOut ?? DBNull.Value);
            command.Parameters.AddWithValue("$cachedRead", (object?)cachedRead ?? DBNull.Value);
            command.Parameters.AddWithValue("$cachedWrite", (object?)cachedWrite ?? DBNull.Value);
            command.Parameters.AddWithValue("$estimated", estimated ? 1 : 0);
            command.Parameters.AddWithValue("$warnings", (object?)warnings ?? DBNull.Value);
            command.ExecuteNonQuery();

            long id = (long)Scalar(connection, transaction!, "SELECT last_insert_rowid()");
            if (promptText is not null)
            {
                // The writer populates FTS on insert; direct seeding must do the same or any
                // q= filter would silently drop the row (the FTS join only happens when the
                // query sanitizes to something).
                using SqliteCommand fts = connection.CreateCommand();
                fts.Transaction = transaction;
                fts.CommandText = "INSERT INTO requests_fts (rowid, prompt_text) VALUES ($rowid, $prompt)";
                fts.Parameters.AddWithValue("$rowid", id);
                fts.Parameters.AddWithValue("$prompt", promptText);
                fts.ExecuteNonQuery();
            }

            return id;
        }

        private static object Scalar(SqliteConnection connection, SqliteTransaction transaction, string sql)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            return command.ExecuteScalar()!;
        }

        private SqliteConnection OpenWrite()
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = DbPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
            }.ToString());
            connection.Open();
            return connection;
        }

        public void Dispose()
        {
            // The read store's pooled (read-only) connections keep the database file open;
            // release them before removing the temp directory.
            SqliteConnection.ClearAllPools();
            Directory.Delete(_dir, recursive: true);
        }
    }
}