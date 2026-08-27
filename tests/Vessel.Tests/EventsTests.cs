using System.Net;
using System.Text.Json;
using Xunit;

namespace Vessel.Tests;

/// <summary>
/// Phase 3 §3 U5–U6 — the SSE lifecycle feed (D5): a hand-rolled line parser is used
/// instead of a library so these tests exercise exactly the wire format
/// <see cref="Vessel.Api.EventsEndpoint"/> writes.
/// </summary>
public class EventsTests
{
    private static CancellationToken CT => TestContext.Current.CancellationToken;

    /// <summary>One open <c>/vessel/api/events</c> connection; reads can be resumed across multiple calls.</summary>
    private sealed class SseReader : IDisposable
    {
        private readonly HttpResponseMessage _response;
        private readonly StreamReader _reader;

        private SseReader(HttpResponseMessage response, StreamReader reader)
        {
            _response = response;
            _reader = reader;
        }

        public static async Task<SseReader> OpenAsync(HttpClient client, string url, CancellationToken ct)
        {
            HttpResponseMessage response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            Stream stream = await response.Content.ReadAsStreamAsync(ct);
            return new SseReader(response, new StreamReader(stream));
        }

        /// <summary>Reads until <paramref name="count"/> named events have arrived (":" heartbeat comments are skipped).</summary>
        public async Task<List<(string Event, string Data)>> ReadEventsAsync(int count, CancellationToken ct)
        {
            var events = new List<(string, string)>();
            string? currentEvent = null;
            var dataLines = new List<string>();

            while (events.Count < count)
            {
                string? line = await _reader.ReadLineAsync(ct);
                if (line is null)
                {
                    break;
                }

                if (line.Length == 0)
                {
                    if (currentEvent is not null)
                    {
                        events.Add((currentEvent, string.Join("\n", dataLines)));
                    }

                    currentEvent = null;
                    dataLines.Clear();
                    continue;
                }

                if (line.StartsWith("event:", StringComparison.Ordinal))
                {
                    currentEvent = line["event:".Length..].Trim();
                }
                else if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    dataLines.Add(line["data:".Length..].Trim());
                }
            }

            return events;
        }

        public void Dispose()
        {
            _reader.Dispose();
            _response.Dispose();
        }
    }

    // U5: one proxied streamed request yields started -> first_token -> completed with a
    // matching seq, and completed.row.id shows up in a subsequent list fetch.
    [Fact]
    public async Task Sse_StartedFirstTokenCompleted_MatchingSeq_RowInListAfterward()
    {
        await using TestVessel vessel = await TestVessel.StartAsync();
        using var client = new HttpClient();

        using SseReader sse = await SseReader.OpenAsync(client, $"{vessel.BaseUrl}/vessel/api/events", CT);
        await Task.Delay(50, CT); // subscription registers a moment after headers flush; safety margin

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(CT);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        Task<List<(string Event, string Data)>> eventsTask = sse.ReadEventsAsync(3, cts.Token);

        using HttpResponseMessage resp = await client.GetAsync($"{vessel.BaseUrl}/sse?n=3&delayMs=30&sseflow", CT);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        await resp.Content.ReadAsByteArrayAsync(CT);

        List<(string Event, string Data)> events = await eventsTask;

        (string Event, string Data) started = events.First(e => e.Event == "started");
        using JsonDocument startedDoc = JsonDocument.Parse(started.Data);
        long seq = startedDoc.RootElement.GetProperty("seq").GetInt64();
        Assert.Contains("sseflow", startedDoc.RootElement.GetProperty("path").GetString());

        (string Event, string Data) firstToken = events.First(e => e.Event == "first_token");
        using JsonDocument ftDoc = JsonDocument.Parse(firstToken.Data);
        Assert.Equal(seq, ftDoc.RootElement.GetProperty("seq").GetInt64());
        Assert.InRange(ftDoc.RootElement.GetProperty("ttftMs").GetDouble(), 0, 10_000);

        (string Event, string Data) completed = events.First(e => e.Event == "completed");
        using JsonDocument compDoc = JsonDocument.Parse(completed.Data);
        Assert.Equal(seq, compDoc.RootElement.GetProperty("seq").GetInt64());
        JsonElement row = compDoc.RootElement.GetProperty("row");
        Assert.NotEqual(JsonValueKind.Null, row.ValueKind);
        long rowId = row.GetProperty("id").GetInt64();

        using HttpResponseMessage listResp = await client.GetAsync($"{vessel.BaseUrl}/vessel/api/requests", CT);
        using JsonDocument listDoc = JsonDocument.Parse(await listResp.Content.ReadAsStringAsync(CT));
        bool found = listDoc.RootElement.GetProperty("rows").EnumerateArray()
            .Any(r => r.GetProperty("id").GetInt64() == rowId);
        Assert.True(found, "the completed event's row id should be present in a subsequent /requests list");
    }

    // U6: a subscriber that never reads its stream must never back-pressure the request
    // path or other subscribers (bounded, drop-oldest); disconnecting mid-stream doesn't
    // fault the hub — later requests and the still-open subscriber keep working.
    [Fact]
    public async Task Sse_SlowSubscriberNeverBlocks_DisconnectDoesNotFaultHub()
    {
        await using TestVessel vessel = await TestVessel.StartAsync();
        using var slowClient = new HttpClient();
        using var activeClient = new HttpClient();
        using var requestClient = new HttpClient();

        // Connects but never reads — its 256-capacity bounded channel will overflow and
        // must drop-oldest rather than block the publisher.
        HttpResponseMessage slowResponse = await slowClient.GetAsync(
            $"{vessel.BaseUrl}/vessel/api/events", HttpCompletionOption.ResponseHeadersRead, CT);

        using SseReader active = await SseReader.OpenAsync(activeClient, $"{vessel.BaseUrl}/vessel/api/events", CT);
        await Task.Delay(50, CT);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(CT);
        cts.CancelAfter(TimeSpan.FromSeconds(30));

        // Flood well past the bounded channel's capacity — none of this may block, and
        // the request path must stay fast throughout.
        const int floodCount = 300;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < floodCount; i++)
        {
            using HttpResponseMessage r = await requestClient.GetAsync($"{vessel.BaseUrl}/echo?flood{i}", CT);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        }

        stopwatch.Stop();
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(30),
            $"flooding {floodCount} requests took {stopwatch.Elapsed} — a slow subscriber may be back-pressuring the request path");

        // The active subscriber still receives events despite the slow one never draining.
        List<(string Event, string Data)> events = await active.ReadEventsAsync(1, cts.Token);
        Assert.NotEmpty(events);

        // Disconnecting the slow subscriber mid-stream must not fault the hub.
        slowResponse.Dispose();
        await Task.Delay(200, CT); // give the server a moment to notice the aborted connection

        using HttpResponseMessage afterDisconnect = await requestClient.GetAsync($"{vessel.BaseUrl}/echo?afterdisconnect", CT);
        Assert.Equal(HttpStatusCode.OK, afterDisconnect.StatusCode);

        List<(string Event, string Data)> moreEvents = await active.ReadEventsAsync(1, cts.Token);
        Assert.NotEmpty(moreEvents);
    }
}
