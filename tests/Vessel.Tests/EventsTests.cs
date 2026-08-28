using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Vessel.Capture;
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
                    // Skip the `hello` frame (H0b server identity): these tests count lifecycle
                    // events, and hello is not one — it is the connection's first frame.
                    if (currentEvent is not null && currentEvent != "hello")
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

        // D05 — `started` carries the session so the UI can scope in-flight rows without
        // guessing; it must match the session the row is ultimately stored under.
        long startedSessionId = startedDoc.RootElement.GetProperty("sessionId").GetInt64();

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

        // The scoping the UI relies on is only correct if these agree.
        Assert.Equal(startedSessionId, row.GetProperty("sessionId").GetInt64());
        // ...and the in-flight row must show the same start time as its stored row (the
        // detail pane hands over from the live entry to the row on completion). Reconciliation
        // itself is now server-authoritative by seq (R11/F2, GET /active), not a startedAt match.
        Assert.Equal(
            startedDoc.RootElement.GetProperty("startedAt").GetString(),
            row.GetProperty("startedAt").GetString());

        using HttpResponseMessage listResp = await client.GetAsync($"{vessel.BaseUrl}/vessel/api/requests", CT);
        using JsonDocument listDoc = JsonDocument.Parse(await listResp.Content.ReadAsStringAsync(CT));
        bool found = listDoc.RootElement.GetProperty("rows").EnumerateArray()
            .Any(r => r.GetProperty("id").GetInt64() == rowId);
        Assert.True(found, "the completed event's row id should be present in a subsequent /requests list");
    }

    // Post-Phase-4 addition (ui-spec.md §9.1, phase-3.md D5): request_ready carries the
    // real model, off the request path, between started and first_token.
    [Fact]
    public async Task Sse_RequestReady_CarriesModel_BetweenStartedAndFirstToken()
    {
        await using TestVessel vessel = await TestVessel.StartAsync();
        using var client = new HttpClient();

        using SseReader sse = await SseReader.OpenAsync(client, $"{vessel.BaseUrl}/vessel/api/events", CT);
        await Task.Delay(50, CT); // subscription registers a moment after headers flush; safety margin

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(CT);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        Task<List<(string Event, string Data)>> eventsTask = sse.ReadEventsAsync(4, cts.Token);

        // initialDelayMs gives the stub a floor closer to a real backend's TTFT — on a warm
        // loopback connection it can otherwise answer in well under a millisecond, which
        // request_ready's background hand-off (channel → dedicated loop → parse → publish)
        // can lose to by sheer bad luck even though it's already the fast path. Real
        // traffic never has a sub-millisecond TTFT, so this isn't papering over a real
        // ordering bug — it's giving the test a realistic race instead of an artificial one.
        var request = new HttpRequestMessage(HttpMethod.Post, $"{vessel.BaseUrl}/sse?n=3&delayMs=30&initialDelayMs=20&modelcheck")
        {
            Content = new StringContent("""{"model":"test-model-xyz"}""", System.Text.Encoding.UTF8, "application/json"),
        };
        using HttpResponseMessage resp = await client.SendAsync(request, CT);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        await resp.Content.ReadAsByteArrayAsync(CT);

        List<(string Event, string Data)> events = await eventsTask;

        int startedIndex = events.FindIndex(e => e.Event == "started");
        int requestReadyIndex = events.FindIndex(e => e.Event == "request_ready");
        int firstTokenIndex = events.FindIndex(e => e.Event == "first_token");
        Assert.True(startedIndex >= 0, "expected a started event");
        Assert.True(requestReadyIndex >= 0, "expected a request_ready event");
        Assert.True(firstTokenIndex >= 0, "expected a first_token event");
        Assert.True(startedIndex < requestReadyIndex, "request_ready should arrive after started");
        Assert.True(requestReadyIndex < firstTokenIndex, "request_ready should arrive before first_token");

        using JsonDocument startedDoc = JsonDocument.Parse(events[startedIndex].Data);
        long seq = startedDoc.RootElement.GetProperty("seq").GetInt64();

        using JsonDocument readyDoc = JsonDocument.Parse(events[requestReadyIndex].Data);
        Assert.Equal(seq, readyDoc.RootElement.GetProperty("seq").GetInt64());
        Assert.Equal("test-model-xyz", readyDoc.RootElement.GetProperty("model").GetString());
    }

    // R11 — every frame carries a monotonic `id:`. This is asserted on the raw wire on
    // purpose: the client's gap detection reads `EventSource.lastEventId`, which is empty
    // unless the server actually emits the field, and a silently missing `id:` would leave
    // gap detection permanently dead while every client-side test still passed.
    [Fact]
    public async Task Sse_EveryFrameCarriesMonotonicEventId()
    {
        await using TestVessel vessel = await TestVessel.StartAsync();
        using var client = new HttpClient();

        using HttpResponseMessage response = await client.GetAsync(
            $"{vessel.BaseUrl}/vessel/api/events", HttpCompletionOption.ResponseHeadersRead, CT);
        await using Stream stream = await response.Content.ReadAsStreamAsync(CT);
        using var reader = new StreamReader(stream);
        await Task.Delay(50, CT);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(CT);
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        // Read raw lines until three complete frames have been seen — the first is the
        // id-less `hello` frame (H0b), so two lifecycle frames (started + completed) follow.
        Task<List<string>> linesTask = Task.Run(
            async () =>
            {
                var collected = new List<string>();
                int frames = 0;
                while (frames < 3)
                {
                    string? line = await reader.ReadLineAsync(cts.Token);
                    if (line is null) break;
                    collected.Add(line);
                    if (line.StartsWith("data:", StringComparison.Ordinal)) frames++;
                }

                return collected;
            },
            cts.Token);

        using HttpResponseMessage proxied = await client.GetAsync($"{vessel.BaseUrl}/echo?eventid", CT);
        Assert.Equal(HttpStatusCode.OK, proxied.StatusCode);

        List<string> lines = await linesTask;

        var ids = lines
            .Where(l => l.StartsWith("id:", StringComparison.Ordinal))
            .Select(l => long.Parse(l["id:".Length..].Trim()))
            .ToList();

        // The hello frame has no id:, so only the two lifecycle frames contribute ids.
        Assert.True(ids.Count >= 2, $"expected an id: line per lifecycle frame, got {ids.Count} in:\n{string.Join("\n", lines)}");
        // Monotonically increasing, so a client can tell "next" from "dropped some".
        Assert.Equal(ids.OrderBy(x => x).ToList(), ids);
        Assert.Equal(ids.Distinct().Count(), ids.Count);

        // Within any real frame the id: line must immediately precede its event: line, or
        // EventSource won't associate the two. (Checked on the first id-bearing frame, since
        // the hello frame legitimately carries an event: with no id:.)
        int firstId = lines.FindIndex(l => l.StartsWith("id:", StringComparison.Ordinal));
        Assert.True(
            firstId >= 0 && firstId + 1 < lines.Count && lines[firstId + 1].StartsWith("event:", StringComparison.Ordinal),
            $"id: must immediately precede event: within a frame; lines:\n{string.Join("\n", lines)}");
    }

    // R11/F2 — GET /vessel/api/active is the server-authoritative lifecycle source the
    // client reconciles against. A completed proxied request must leave the active set and
    // advance the completed-seq boundary, so a client can tell a finished request from a
    // running one without inspecting paginated history at all.
    [Fact]
    public async Task Active_CompletedRequestLeavesActiveSet_AndAdvancesBoundary()
    {
        await using TestVessel vessel = await TestVessel.StartAsync();
        using var client = new HttpClient();

        using HttpResponseMessage r = await client.GetAsync($"{vessel.BaseUrl}/echo?active", CT);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        // The completion is emitted by the writer after insert, so poll until the boundary
        // has advanced. Once it has for our single request, the active set must be empty.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(CT);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        long newestCompleted = 0;
        int activeCount = -1;
        while (!cts.IsCancellationRequested)
        {
            using HttpResponseMessage active = await client.GetAsync($"{vessel.BaseUrl}/vessel/api/active", cts.Token);
            using JsonDocument doc = JsonDocument.Parse(await active.Content.ReadAsStringAsync(cts.Token));
            newestCompleted = doc.RootElement.GetProperty("newestCompletedSeq").GetInt64();
            activeCount = doc.RootElement.GetProperty("activeSeqs").GetArrayLength();
            if (newestCompleted >= 1)
            {
                break;
            }

            await Task.Delay(25, cts.Token);
        }

        Assert.True(newestCompleted >= 1, "a completed request must advance newestCompletedSeq");
        Assert.Equal(0, activeCount); // no traffic is in flight, so nothing remains active
    }

    // R11/H0b(1) — every server-identity surface reports the same run id within one process:
    // the SSE `hello` frame (first on the wire, no `id:` so it never perturbs gap detection),
    // `GET /active`, and `GET /status`. A client uses a run-id change to tell a restart from a
    // reconnect and discard a dead process's in-flight seqs wholesale.
    [Fact]
    public async Task ServerRunId_ConsistentAcrossHelloActiveAndStatus()
    {
        await using TestVessel vessel = await TestVessel.StartAsync();
        using var client = new HttpClient();

        // The hello frame is the first thing the SSE endpoint writes.
        using HttpResponseMessage events = await client.GetAsync(
            $"{vessel.BaseUrl}/vessel/api/events", HttpCompletionOption.ResponseHeadersRead, CT);
        await using Stream stream = await events.Content.ReadAsStreamAsync(CT);
        using var reader = new StreamReader(stream);

        string? helloRunId = null;
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(CT))
        {
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            string? currentEvent = null;
            while (helloRunId is null)
            {
                string? line = await reader.ReadLineAsync(cts.Token);
                if (line is null) break;
                if (line.StartsWith("id:", StringComparison.Ordinal))
                {
                    Assert.Fail("the hello frame must not carry an id: field");
                }
                else if (line.StartsWith("event:", StringComparison.Ordinal))
                {
                    currentEvent = line["event:".Length..].Trim();
                }
                else if (line.StartsWith("data:", StringComparison.Ordinal) && currentEvent == "hello")
                {
                    using JsonDocument doc = JsonDocument.Parse(line["data:".Length..].Trim());
                    helloRunId = doc.RootElement.GetProperty("serverRunId").GetString();
                }
            }
        }

        Assert.False(string.IsNullOrEmpty(helloRunId), "expected a hello frame carrying serverRunId");

        using JsonDocument activeDoc = JsonDocument.Parse(
            await (await client.GetAsync($"{vessel.BaseUrl}/vessel/api/active", CT)).Content.ReadAsStringAsync(CT));
        using JsonDocument statusDoc = JsonDocument.Parse(
            await (await client.GetAsync($"{vessel.BaseUrl}/vessel/api/status", CT)).Content.ReadAsStringAsync(CT));

        Assert.Equal(helloRunId, activeDoc.RootElement.GetProperty("serverRunId").GetString());
        Assert.Equal(helloRunId, statusDoc.RootElement.GetProperty("serverRunId").GetString());
    }

    // R23/I0a — a clear is versioned, recoverable state, not just a frame. `GET /active` must
    // report the latest clear's version and the predicate the server actually deleted by, with
    // *no subscriber attached* — that is precisely the client whose `cleared` frame was dropped
    // (or who was reconnecting) and must recover the same deletion state from recovery alone.
    [Fact]
    public async Task Active_ReportsLatestClearVersionAndPredicate_WithoutAnySubscriber()
    {
        await using TestVessel vessel = await TestVessel.StartAsync();
        using var client = new HttpClient();

        using JsonDocument before = JsonDocument.Parse(
            await (await client.GetAsync($"{vessel.BaseUrl}/vessel/api/active", CT)).Content.ReadAsStringAsync(CT));
        Assert.Equal(JsonValueKind.Null, before.RootElement.GetProperty("clear").ValueKind);

        for (int i = 1; i <= 2; i++)
        {
            using HttpResponseMessage r = await client.GetAsync($"{vessel.BaseUrl}/echo?clearme{i}", CT);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        }

        await CaptureDb.WaitUntil(vessel.DbPath, rows => rows.Count, count => count >= 2);

        using (HttpResponseMessage cleared = await client.DeleteAsync($"{vessel.BaseUrl}/vessel/api/requests?scope=all", CT))
        {
            Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        }

        using JsonDocument afterAll = JsonDocument.Parse(
            await (await client.GetAsync($"{vessel.BaseUrl}/vessel/api/active", CT)).Content.ReadAsStringAsync(CT));
        JsonElement clearAll = afterAll.RootElement.GetProperty("clear");
        Assert.Equal(1, clearAll.GetProperty("version").GetInt64());
        Assert.Equal("all", clearAll.GetProperty("scope").GetString());
        Assert.Equal(JsonValueKind.Null, clearAll.GetProperty("beforeTs").ValueKind);
        // Every row the clear deleted is at or below this id — the two above were ids 1 and 2.
        Assert.Equal(2, clearAll.GetProperty("boundaryId").GetInt64());

        string cutoff = DateTime.UtcNow.ToString("o");
        using (HttpResponseMessage clearedBefore = await client.DeleteAsync(
            $"{vessel.BaseUrl}/vessel/api/requests?before={Uri.EscapeDataString(cutoff)}", CT))
        {
            Assert.Equal(HttpStatusCode.OK, clearedBefore.StatusCode);
        }

        using JsonDocument afterBefore = JsonDocument.Parse(
            await (await client.GetAsync($"{vessel.BaseUrl}/vessel/api/active", CT)).Content.ReadAsStringAsync(CT));
        JsonElement clearBefore = afterBefore.RootElement.GetProperty("clear");
        Assert.Equal(2, clearBefore.GetProperty("version").GetInt64()); // monotonic within the run
        Assert.Equal("before", clearBefore.GetProperty("scope").GetString());
        Assert.Equal(cutoff, clearBefore.GetProperty("beforeTs").GetString()); // the DELETE's own predicate
    }

    // R11/H0b(2) — the concurrent-snapshot invariant probe, ported. GetActiveRequests must
    // return one coherent snapshot: every ODD seq at or below the returned watermark must be
    // present in the active set. The probe registers an odd seq that never completes, then
    // registers and completes the following even seq (which advances the watermark past the
    // odd one). Reading the active keys and the watermark separately (a concurrent dictionary +
    // an interlocked long) let a snapshot report a watermark covering a still-running odd seq
    // absent from the keys — the review saw 187/571 snapshots violate this. Under one lock, the
    // invariant holds with zero violations.
    [Fact]
    public async Task Active_SnapshotStaysCoherent_UnderConcurrentRegisterAndComplete()
    {
        var hub = new CaptureEvents();
        var violations = new System.Collections.Concurrent.ConcurrentBag<string>();
        using var stop = new CancellationTokenSource();

        Task[] readers = Enumerable.Range(0, 4).Select(_ => Task.Run(
            () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    ActiveRequests snap = hub.GetActiveRequests();
                    var active = new HashSet<long>(snap.ActiveSeqs);
                    for (long odd = 1; odd <= snap.NewestCompletedSeq; odd += 2)
                    {
                        if (!active.Contains(odd))
                        {
                            violations.Add($"odd {odd} absent though watermark is {snap.NewestCompletedSeq}");
                            break;
                        }
                    }
                }
            },
            CT)).ToArray();

        const int iterations = 4000;
        for (int k = 1; k <= iterations; k++)
        {
            long odd = hub.Register("2026-08-28T00:00:00.0000000Z", 1, "POST", "/odd", "stub", []); // never completes
            long even = hub.Register("2026-08-28T00:00:00.0000000Z", 1, "POST", "/even", "stub", []);
            Assert.Equal((2 * k) - 1, odd); // the hub allocates in order, so the parity holds
            Assert.Equal(2 * k, even);
            hub.Completed(even, null); // advances the watermark past `odd`
        }

        stop.Cancel();
        await Task.WhenAll(readers);

        Assert.Empty(violations);
    }

    // R25/H0b(3) — once capture admission is closed, every proxied request still forwards, and
    // its registered seq must reach a terminal transition (ProxyHandler completes it on the
    // drop) instead of leaking in the active set forever. With and without an SSE subscriber:
    // removal is independent of subscribers.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StoppedAdmission_ProxiedRequestsForward_ButLeaveNoActiveEntries(bool withSubscriber)
    {
        await using TestVessel vessel = await TestVessel.StartAsync();
        using var client = new HttpClient();

        HttpResponseMessage? subscriber = null;
        if (withSubscriber)
        {
            subscriber = await client.GetAsync(
                $"{vessel.BaseUrl}/vessel/api/events", HttpCompletionOption.ResponseHeadersRead, CT);
            await Task.Delay(50, CT); // let the subscription register
        }

        // Enter the terminal admission state directly (the review's probe shape), rather than
        // inducing five real disk failures.
        vessel.Services.GetRequiredService<CaptureChannel>().Stop("test: admission closed");
        var events = vessel.Services.GetRequiredService<CaptureEvents>();

        const int count = 32;
        for (int i = 0; i < count; i++)
        {
            using HttpResponseMessage r = await client.GetAsync($"{vessel.BaseUrl}/echo?stopped{i}", CT);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode); // forwarding is independent of capture health
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(CT);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        while (events.GetActiveRequests().ActiveSeqs.Length > 0 && !cts.IsCancellationRequested)
        {
            await Task.Delay(25, cts.Token);
        }

        Assert.Empty(events.GetActiveRequests().ActiveSeqs);

        subscriber?.Dispose();
    }

    // R26/I1 — the review's real-HTTP repro. With injectStreamUsage on, request preparation
    // reads the body before forwarding; it used to run *above* the handler's try/finally, so a
    // client that vanished mid-upload threw past the finalizer: no record, no `completed`, and
    // the registered seq stranded in the authoritative active set (the viewer showed it running
    // forever, and reconciliation preserved it because the server still listed it). Preparation
    // now runs inside the guarded span, so the aborted upload lands as a captured
    // `client_disconnect` row and the seq reaches a terminal transition.
    [Fact]
    public async Task AbortedUsageInjectionUpload_LandsAsClientDisconnect_AndLeavesNoActiveEntry()
    {
        await using TestVessel vessel = await TestVessel.StartAsync(c =>
        {
            c.Backends["stub"].Type = "openai";
            c.Backends["stub"].InjectStreamUsage = true;
        });

        var events = vessel.Services.GetRequiredService<CaptureEvents>();
        string marker = $"m{Guid.NewGuid():N}";
        var target = new Uri(vessel.BaseUrl);

        // A raw socket, because this is about a half-sent body: announce 4096 bytes, send only a
        // JSON prefix, wait until Vessel has registered the request, then reset the connection.
        using (var tcp = new TcpClient())
        {
            await tcp.ConnectAsync(target.Host, target.Port, CT);
            NetworkStream stream = tcp.GetStream();
            byte[] prefix = System.Text.Encoding.UTF8.GetBytes(
                $"POST /v1/chat/completions?marker={marker} HTTP/1.1\r\n"
                + $"Host: {target.Authority}\r\n"
                + "Content-Type: application/json\r\n"
                + "Content-Length: 4096\r\n\r\n"
                + """{"model":"m","stream":true,"messages":[{"role":"user","content":"tru""");
            await stream.WriteAsync(prefix, CT);
            await stream.FlushAsync(CT);

            DateTime registeredBy = DateTime.UtcNow.AddSeconds(10);
            while (events.GetActiveRequests().ActiveSeqs.Length == 0 && DateTime.UtcNow < registeredBy)
            {
                await Task.Delay(20, CT);
            }

            Assert.NotEmpty(events.GetActiveRequests().ActiveSeqs); // the upload is registered and mid-body

            tcp.LingerState = new LingerOption(true, 0); // RST, not a graceful close
        }

        // Forwarding is independent of any of this: a normal request still succeeds.
        using var client = new HttpClient();
        using HttpResponseMessage control = await client.GetAsync($"{vessel.BaseUrl}/echo?control{marker}", CT);
        Assert.Equal(HttpStatusCode.OK, control.StatusCode);

        // The aborted request reached a terminal transition — nothing left registered.
        DateTime terminalBy = DateTime.UtcNow.AddSeconds(10);
        while (events.GetActiveRequests().ActiveSeqs.Length > 0 && DateTime.UtcNow < terminalBy)
        {
            await Task.Delay(25, CT);
        }

        Assert.Empty(events.GetActiveRequests().ActiveSeqs);

        // ...and it was captured, with the interrupted-request error policy intact.
        CapturedRow row = await CaptureDb.WaitForRow(vessel.DbPath, r => r.Path.Contains(marker) && r.Method == "POST");
        Assert.Equal(Vessel.Api.VesselErrors.ClientDisconnect, row.Error);
        Assert.Null(row.StatusCode); // no response was ever started
    }

    // R22 — the review's concurrent-publisher probe, ported as a regression test. An atomic
    // id counter makes ids *unique* but not *ordered*: before the publish lock, two threads
    // could allocate ids N and N+1 and enqueue them reversed, and a client reads a reversal
    // as frame loss. Each batch stays under the 256 subscriber capacity and is fully drained
    // before the next, so no frame is legitimately dropped — every reversal here would be a
    // real ordering defect. The review saw 3,535 adjacent reversals across 12,800 events.
    [Fact]
    public async Task Publish_ConcurrentPublishers_DeliversEveryFrameInStrictIdOrder()
    {
        var hub = new CaptureEvents();
        using CaptureSubscription subscription = hub.Subscribe();
        ChannelReader<SseEvent> reader = subscription.Reader;

        const int publishers = 16;
        const int batches = 100;
        const int perBatch = 128; // < 256 capacity, so a fully-drained batch never drops
        const int perPublisher = perBatch / publishers;

        var ids = new List<long>(batches * perBatch);
        long seq = 0;

        for (int b = 0; b < batches; b++)
        {
            var tasks = new Task[publishers];
            for (int p = 0; p < publishers; p++)
            {
                tasks[p] = Task.Run(
                    () =>
                    {
                        for (int k = 0; k < perPublisher; k++)
                        {
                            hub.Completed(Interlocked.Increment(ref seq), null);
                        }
                    },
                    CT);
            }

            await Task.WhenAll(tasks);

            // Drain this batch before the next so the bounded queue never overflows.
            for (int i = 0; i < perBatch; i++)
            {
                SseEvent evt = await reader.ReadAsync(CT);
                ids.Add(evt.Id);
            }
        }

        Assert.Equal(batches * perBatch, ids.Count);

        // Strictly increasing = zero adjacent reversals; equal to 1..N = complete delivery,
        // no gaps. Both properties in one assertion, and both fail without the publish lock.
        int reversals = 0;
        for (int i = 1; i < ids.Count; i++)
        {
            if (ids[i] <= ids[i - 1])
            {
                reversals++;
            }
        }

        Assert.Equal(0, reversals);
        Assert.Equal(Enumerable.Range(1, ids.Count).Select(x => (long)x), ids);
    }

    // R22 — the complement: a genuine overflow (nobody draining, past the 256 capacity) still
    // drops oldest, and that loss stays *detectable* — surviving ids are ordered but start
    // past 1, exactly the id gap the client keys reconciliation off.
    [Fact]
    public void Publish_OverflowingCapacity_DropsOldest_AsADetectableIdGap()
    {
        var hub = new CaptureEvents();
        using CaptureSubscription subscription = hub.Subscribe();
        ChannelReader<SseEvent> reader = subscription.Reader;

        const int total = 400; // > 256 capacity, and nothing is reading, so oldest frames drop
        for (int i = 1; i <= total; i++)
        {
            hub.Completed(i, null);
        }

        var ids = new List<long>();
        while (reader.TryRead(out SseEvent? evt))
        {
            ids.Add(evt.Id);
        }

        Assert.InRange(ids.Count, 1, total - 1); // some, but not all, survived
        for (int i = 1; i < ids.Count; i++)
        {
            Assert.True(ids[i] > ids[i - 1], "surviving frames remain in id order");
        }

        Assert.True(ids[0] > 1, "oldest frames were dropped, so the id sequence starts past 1 — a detectable gap");
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
