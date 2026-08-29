# Phase 3 — Minimal UI: Implementation Spec

> Expands Phase 3 of [plan.md](plan.md). Design authority is [architecture.md](architecture.md);
> this spec makes the concrete decisions that document deliberately left open.
>
> **Goal:** stop using the SQLite browser. A browser tab on `/vessel/` shows live
> in-flight requests as agents run; clicking a row answers "what did it send and what
> came back". Raw-JSON rendering is enough this phase — rich message rendering is
> Phase 4.

## 0. Scope

**In:** frontend scaffold (Vite + React + TS + Tailwind + shadcn/ui) embedded and served
under `/vessel/` (§10, §11); read-side REST API (list with cursor paging, detail, stats)
(§7); SSE lifecycle events + live in-flight rows with running timers (§4.4); sessions
activated end-to-end — marker rows, `session_id` stamping, stats bar, Reset (§6.3);
publish pipeline runs the frontend build.

**Out (explicitly):** free-text search and the full filter bar (Phase 4 — the list
endpoint's *shape* is forward-compatible, but Phase 3 wires only `limit`/`before`/
`session`), rendered message view and tool-call cards (Phase 4 — raw pretty-printed
JSON only), config editor page, clear-down UI, replay/diff/curl, rate-limit display,
Ollama panel, cost estimates.

**No schema migration** — `sessions` exists and `requests.session_id` exists; this
phase starts populating them.

---

## 1. Key implementation decisions

Deviations found during implementation go back into this file and, if architectural,
into architecture.md §12.

### D1 — Frontend lives in `frontend/`, embedded at publish, Vite dev server for dev

- `frontend/` is a standard Vite + React + TypeScript app: Tailwind, shadcn/ui
  (generated components **committed** — shadcn is a code generator, not a runtime
  dependency), TanStack Query, TanStack Virtual, lucide-react. **No router** — Vessel
  is one screen; don't add react-router for tabs.
- **Serving:** `frontend/dist/**` is embedded into the Vessel assembly
  (`EmbeddedResource` + `ManifestEmbeddedFileProvider`) and served at `/vessel/`.
  Hashed assets get `Cache-Control: immutable`; `index.html` gets `no-cache`.
  When no dist is embedded (dev binary built without the frontend), `/vessel/`
  returns a small built-in placeholder page pointing at the dev workflow — the
  backend must never fail to build or run because the frontend wasn't built.
- **Build integration:** `dotnet publish` runs `npm ci && npm run build` via a
  `BeforeTargets="PrepareForPublish"` MSBuild target. Plain `dotnet build` / `dotnet
  test` never touch npm — the .NET dev loop and CI tests stay Node-free. (Publishing
  requires Node; that's acceptable and goes in the Phase 6 README.)
- **Dev workflow:** `npm run dev` starts Vite with a proxy for `/vessel/api` →
  `http://127.0.0.1:4550` (SSE proxies fine). Same-origin via proxy → no CORS
  anywhere.

### D2 — Read side: separate read-only connections, never the writer's

The writer keeps its single exclusive connection (`Pooling=false`). Reads use a new
`SqliteReadStore` opening `Mode=ReadOnly` pooled connections per call — WAL makes
this safe concurrently with the writer. API handlers never touch `SqliteCaptureStore`.
All read queries are indexed (`id` cursor, `session_id`); no query may scan bodies.

### D3 — API shapes (all under `/vessel/api/`, STJ source-generated, camelCase)

- `GET /requests?limit=100&before={id}&session={id}` → `{ rows: Summary[], nextBefore: number|null }`.
  Reverse-chron by `id`. `limit` default 100, max 500. **Summary** = every column
  except header/body blobs: `id, startedAt, sessionId, backend, tags, method, path,
  format, model, statusCode, error, streamed, replayOf, durationMs, ttftMs,
  vesselOverheadMs, tokPerSec, tokensIn, tokensOut, tokensCachedRead,
  tokensCachedWrite, tokensEstimated, stopReason, warnings, truncated`.
  Filter params beyond these three are Phase 4; the response shape doesn't change.
- `GET /requests/{id}` → Summary + `requestHeaders`, `responseHeaders` (parsed JSON
  objects), `requestBody`, `responseBody`, `responseRaw`. Bodies are decompressed
  server-side; each is `{ text: string }` when valid UTF-8 else `{ base64: string }`,
  plus the UI shows raw vs reassembled for streamed rows. 404 JSON for unknown id.
  **Post-Phase-4 addition** (code review R05/D01, phase-2 D3): "decompressed" now means
  storage-level zstd *and* the body's own `Content-Encoding` — storage stays wire-true,
  so this endpoint is where a gzip/br body becomes readable. That decode is bounded by
  `capture.maxBodyMb`; when it hits the budget the body gains
  `decodeTruncated: true` (omitted otherwise) so a prefix is never shown as a whole
  document.
- `GET /stats?session={id|current|all}` (default `current`) →
  `{ total, failed, avgDurationMs, avgTokPerSec, avgTtftMs, sessionId, sessionStartedAt }`.
  `failed` = `error` set or status ≥ 400. Averages over non-null values only;
  `avgTtftMs` over streamed rows.
  **Post-Phase-4 addition** (ui-spec.md §9.1 token-totals TODO, implemented): the
  response gains `tokensIn`, `tokensOut`, `tokensCachedRead`, `tokensCachedWrite`
  (`SUM`s over the same scope, `COALESCE(...,0)` — null-safe → 0) and
  `tokensEstimated` (`COALESCE(MAX(tokens_estimated), 0)` — true iff any
  contributing row has `tokens_estimated = 1` — the totals are then estimates and
  the UI renders them `~`-prefixed).
- `GET /sessions` → newest-first list; `POST /sessions` → creates a marker (optional
  `{name}`), returns it. No delete.
  **Post-Phase-4 addition** (code review R06): `POST /sessions` and
  `DELETE /requests` are executed by the background writer, so they now answer
  **503 `capture_stopped`** if the writer has given up rather than awaiting a completion
  nobody will resolve; both also honour client cancellation. `GET /vessel/api/status`
  gains `capture: { recording: bool, stoppedReason?: string }` so that state is
  observable (the UI banner is a separate frontend item).
- ~~**Re-review addition** (Batch F, R23): `DELETE /requests` response gains
  `boundaryId?: number`~~ — **retired by Batch H (H0a)**. An id boundary cannot describe a
  clear-before (ids follow persistence order, not start time), and an ack cannot be ordered
  against completions at all. `DELETE /requests` returns only `{ deleted: number }`, for the
  UX toast.
  **Fifth-round correction (Batch J, J0):** deletion scope no longer travels to the client in
  *any* form — not on the ack, not on the frame, not on `/active`. Every predicate model was
  wrong in some ordering, and the client is not the right place to decide which rows a past
  deletion removed. A clear reaches the client as a **position** in the event log; the rows
  that survived it come back from the refetch that position triggers, which reads the
  post-clear database (D5 below).
- Errors follow the Phase 0 convention (`X-Vessel-Error` + `{"error":{...}}`).

### D4 — Sessions: current-session id is stamped at capture time

A `CurrentSession` singleton holds the active session id. Startup: newest `sessions`
row, creating row 1 ("session 1") on a fresh DB — done during writer init, before
traffic. `POST /sessions` inserts the marker (via a store call that's writer-thread-safe,
see below) and updates the singleton. **`CaptureRecord` gains `SessionId`, read from the
singleton at request start** — a record enqueued before a Reset but flushed after keeps
the session it started in. The writer stamps the column on insert.

Session-marker insert happens off the writer thread (an API handler). To keep the
single-writer invariant, `POST /sessions` doesn't touch SQLite directly: it enqueues a
control message and the writer executes the insert — implement as a small
`Func<SqliteCaptureStore>`-style command on the existing channel (a union type:
`CaptureRecord | WriterCommand`), completing a `TaskCompletionSource` with the new id
so the API can respond with it. No second write connection, no lock dance.

### D5 — SSE lifecycle events: `/vessel/api/events`

- Every capture gets a process-lifetime sequence number `seq` (int64 counter),
  carried on `CaptureContext`/`CaptureRecord` — the correlation key while a request
  has no DB id yet.
  **Fourth-round correction (Batch I, I0b(1)):** that counter lives on the
  `CaptureEvents` hub, not on `CaptureContext`, and is allocated *inside* `Register`
  under the publish lock — allocation **is** registration. When the seq was allocated in
  the `CaptureContext` constructor, a handler could be descheduled between "seq exists"
  and "seq registered"; a later request could complete in that window and advance the
  watermark past the unregistered seq, and a snapshot taken there reported it neither
  active nor unfinished — so reconciliation expired a request that was about to run
  (the review reproduced exactly this with production types). Atomic allocation makes
  that interleaving unrepresentable — which is what keeps a snapshot honest under Batch J
  too: a seq that exists is registered, so it cannot be silently missing from one.
  ~~That is what lets the client's rule "absent from the active set **and** at/below the
  boundary ⇒ finished" be sound.~~ **Superseded by J0:** the client no longer compares seqs
  against a boundary at all; it adopts the snapshot's active set wholesale and orders its own
  pending work against the snapshot's log position.
- Events (named SSE events, JSON data): `started` `{seq, startedAt, sessionId, method,
  path, backend, tags}` — emitted at handler entry; `first_token` `{seq, ttftMs}` — emitted
  on the first-response-byte mark of streamed responses; `completed` `{seq, row:
  Summary}` — emitted by the **writer after the row is inserted** (so it carries the
  real DB id and enriched fields). A dropped batch (writer resilience path) emits
  `completed` with `row: null` so the UI can clear the in-flight entry.
- Broadcast: `CaptureEvents` singleton; each SSE connection gets its own bounded
  channel (capacity 256, `DropOldest`) — a stalled browser can never back-pressure the
  request path or the writer. Comment heartbeat (`: ping`) every 15 s. No replay: the
  UI loads history via REST *after* subscribing, then reconciles by `id`/`seq`
  (duplicates resolved in favor of REST rows).
- Request-path emit cost is a non-blocking `TryWrite` per subscriber; zero subscribers
  = near-zero cost. `/vessel/*` traffic never emits events (it is never captured).
- **Post-Phase-4 addition** (code review R11/R22/D05, implemented):
  - Every frame carries the hub's monotonic publish sequence as the SSE **`id:`** field.
    Dropping oldest is deliberate, but it was previously *undetectable*: a lost
    `completed` left an in-flight row running forever with no signal. A client seeing the
    id jump knows it missed frames and reconciles. The id is deliberately **not** the
    request `seq` — `seq` is assigned at request *start*, so a legitimately long-running
    request trails the newest `seq` arbitrarily far, and any distance heuristic on it
    would expire real in-flight requests.
  - **Re-review (Batch F, R22): publish is ordered.** The id is allocated *inside* the
    hub's publish lock, in the same critical section as the channel fan-out, so every
    subscriber observes ids strictly increasing. An atomic counter alone made ids unique
    but not ordered — two publishers could allocate `N`/`N+1` and enqueue them reversed,
    which the client reads as loss and answers with a needless reconciliation per reversal
    (the review measured 3,535 reversals in 12,800 events). The client also never rewinds
    its id watermark, and coalesces a burst of gaps (debounced, single-flight) into one
    reconciliation rather than one per gap.
  - **Re-review (Batch F, R11): reconciliation is server-authoritative**, not
    history-derived. The old approach dropped in-flight entries the *refreshed history
    pages* accounted for — but a completion off the loaded pages, filtered out, or for a
    since-cleared row is invisible there, so those rows never cleared. Reconciliation now
    fetches `GET /vessel/api/active` (below) and removes any in-flight row the server no
    longer lists as active; a genuinely long-running request survives because it is
    genuinely in the set (never expired by a timer). It then refetches list + stats +
    facets once. The `(startedAt, method, path)` identity correlation the old path used is
    gone — removal is keyed directly on `seq`.
  - `started` carries `sessionId` (D05), so the UI scopes in-flight rows to the viewed
    session accurately instead of guessing.
- **Re-review addition** (Batch F, R11/F2; extended by Batches H and I, **replaced in
  Batch J**, **enriched in Batch K**, **extended in Batch L (R27)**) — `GET /vessel/api/active` →
  `{ active: ActiveDescriptor[], logPosition: number, serverRunId: string }`, where
  `ActiveDescriptor` is `{ seq, startedAt, sessionId, method, path, backend, tags, model, ttftMs }`
  — the `started` frame's own payload, plus `model` once `request_ready` has parsed one and
  `ttftMs` once `first_token` has fired (both null until then). The server-authoritative
  in-flight set, sourced from the live `CaptureEvents` hub: an entry is added on `started` and
  removed on `completed`, independent of any SSE subscriber. **K0b:** it carries descriptors
  rather than bare seqs because a recovering client has to *render* what it is told is running,
  and the frame that would have supplied the method, path, start time, session and tags is
  exactly the frame a bounded drop-oldest queue may have dropped. **R27:** the same is true of
  the live TTFT a `first_token` frame carries — `FirstToken` now updates the locked descriptor
  the same way `RequestReady` does, under the same publish lock, so a dropped `first_token`
  frame no longer loses that metric on recovery. The registry already receives every field at
  registration; the terminal invariant (H0b(3)) already guarantees each entry is removed.
  Deliberately separate from `/status` (which is polled and cached) —
  this changes on every request and is fetched on demand during recovery. `logPosition`
  (J0) is the newest SSE publish id allocated when the snapshot was taken, so the response is
  *lifecycle truth as of one stream position*. `serverRunId` (Batch H) identifies the process
  lifetime — seqs **and** positions restart with the process — so a client discards a snapshot
  from a different Vessel run outright. All three fields are read under the hub's one lock, so
  the snapshot is coherent (H0b(2)).
  ~~`newestCompletedSeq`: the boundary below which an absent `seq` is definitely finished.~~
  **Removed in Batch J.** A watermark orders a client's *rendered* rows against the snapshot
  but says nothing about the work it is still holding — the queued frame, the buffered
  completion, the outstanding fetch — which is where the fifth round's four failures lived. A
  log position orders all of it by the same arithmetic.
- **Third-round re-review (Batch H, R11/R23/R25)** — the lifecycle authority gains
  identity, coherence, and a terminal invariant, and clearing becomes an in-band event:
  - **`hello` event (H0b(1)).** Every SSE connection's *first* frame is
    `event: hello` / `data: {serverRunId}`, carrying this Vessel process's run id (a
    fresh GUID per process). It deliberately carries **no `id:` field**, so it never
    perturbs the gap-detection watermark. `serverRunId` is also on `GET /active` and
    `GET /status`. A run-id change across a reconnect means Vessel *restarted*: the
    client's in-flight `seq`s are from a dead process (process-lifetime `seq`s reset, so
    an old high `seq` sits above the fresh process's low watermark and looks "just
    started"), so the client discards its whole in-flight map rather than boundary-
    comparing across lifetimes. This replaces the previous reliance on the reconnect
    handler alone, which could not distinguish a restart from an ordinary reconnect.
  - **Coherent active snapshot (H0b(2)).** The in-flight set, the publish id, and the
    fan-out are all guarded by the *one* hub lock, so `GET /active` returns a single coherent
    snapshot. Read separately (a concurrent dictionary + an interlocked long, as before) a
    snapshot could pair a position with an active set from a different moment — the review's
    187/571 torn-snapshot probe — and recovery would then wrongly expire a legitimate request.
    Under J0 this coherence is the contract itself: "every frame at or below `logPosition` is
    already reflected in the snapshot's descriptors" is only true if the two are read together
    — which now includes the model `request_ready` records (K0b).
  - **Terminal invariant (H0b(3), R25).** "Registered → terminal" is owned at the
    registration site. `started` registers the `seq`; the writer normally removes it via
    `completed`. When capture admission is closed (the writer gave up), `ProxyHandler`'s
    `finally` completes the dropped capture itself (`completed{row:null}`), and the
    writer's give-up/drain path completes every capture identity it discards. Forwarding
    stays independent of capture health. Without this, every proxied request after a
    give-up leaked a permanent active-set entry (the review's 32-retained-seqs probe).
  - **`cleared` event (H0a, R23; payload replaced in Batch J).** Clearing is an in-band SSE
    frame: `event: cleared` / `data: {}`. The writer publishes it at clear-commit time under
    the same lock as `completed`, so a row a clear deletes is always seen `completed` *before*
    `cleared` (it had to be inserted to be deleted) — that ordering is retained and is what
    lets ordered replay divide the completions a clear removes from the ones it does not.
    ~~`data: {version, scope, beforeTs, boundaryId}`, which the client purged listed and
    buffered rows by.~~ **Superseded by J0:** the frame carries no predicate and the server
    retains none. It **retires** the Batch F3 boundary/generation model (the `DELETE /requests`
    ack's `boundaryId` was unsound: ids follow persistence order, not start time) and, with
    J0, the I0a versioned-predicate model that replaced it.
- ~~**Fourth-round re-review (Batch I, I0a/R23) — a clear is versioned, recoverable state;
  the frame is only the fast path.**~~ The hub kept the latest clear as
  `{version, scope, beforeTs, boundaryId}`, reported it on `/active`, and the client re-applied
  that predicate on arrival, once more when every list fetch outstanding at that moment had
  settled, and on the completion buffer's drain, exempting rows whose completion was published
  after the clear. **Retired in Batch J**, having failed the fifth round in three ways at once
  (review §2.2): a queued completion was misclassified as post-clear merely because it was
  *applied* after the clear was learned; a valid row was purged for reusing a cleared id when
  no completion frame survived to exempt it; and a later, narrower clear overwrote the record
  of an earlier missed one, since the hub retained only the latest. Recorded here because the
  reasoning matters: a client cannot re-derive which rows a past deletion removed, and every
  fix that tried made the next ordering worse.
- **Fifth-round re-review (Batch J, J0) — recovery is a snapshot plus an ordered log.** One
  mechanism replaces the clear predicates, the completed-seq boundary and the provenance set:
  - The SSE **event id is the single log position** for every lifecycle change, `cleared`
    included. It is allocated under the publish lock, so it orders every change against every
    other, and `GET /active` reports the position its active set is true as of.
  - **Recovery is wholesale replacement.** On reconnect, gap or run change the client fetches
    the snapshot, then discards its in-flight map, its **entire** completion buffer and every
    frame it holds at or below `logPosition`; rebuilds its in-flight rows from the snapshot's
    descriptors (K0b — no intersection with what it happens to have seen); and
    refetches list/stats/facets. Discarding is sound by construction: a frame the client
    received before it issued the request was published before the server took the snapshot,
    so its id is at or below that position — the snapshot, and the database the refetch reads,
    already account for it. Frames above the position replay in order on top.
  - **Between recoveries, ordered replay only.** Frames apply in id order; `cleared` drops the
    cached rows and the buffer at its position and schedules a refetch. Any detected gap goes
    to recovery — never to ad-hoc reasoning about what was missed.
  - **REST reads are authoritative and never client-filtered.** Nothing the client holds
    deletes a row a fetch returned. A clear or recovery always starts a *new* fetch after
    itself, and the last-started fetch wins. **Sixth-round correction (K0a):** "new" is
    enforced by cancelling the outstanding list read first, then refetching. `refetchQueries`
    alone does not start a second request while a query's *initial* fetch is pending with no
    data — TanStack v5 returns that pending promise — so the rule silently degraded to "first
    fetch wins" and a pre-clear snapshot could land as the authoritative answer, whether the
    clear arrived in-band or was learned through recovery. Cancellation settles that request as
    discarded and reaches the network, because the list query passes TanStack's `signal` to
    `fetch`. No row is inspected or filtered: that stance is unchanged. **Accepted trade, part
    of the contract:** a stale pre-clear fetch may display briefly until its superseding refetch
    settles. Settled state always converges, which is what every review case asserts.
  - ~~**Display limit, recorded:** in-flight rows are rendered only for seqs the client has
    `started` details for.~~ **Fixed in Batch K (K0b)**, not a caveat: the snapshot describes
    each active request, so a request whose `started` frame the feed dropped is displayed after
    recovery rather than staying invisible until it completes.
  - ~~**Omission, recorded:** the descriptor does not carry TTFT (a `first_token` datum, not
    registration metadata); a dropped `first_token` frame left a recovered row without its
    live TTFT.~~ **Fixed in Batch L (R27):** `FirstToken` now updates the same locked
    descriptor `RequestReady` does, so `/active` returns the live TTFT once measured and
    recovery rebuilds it even when the `first_token` frame itself was dropped.
- **Fourth-round re-review (Batch I, I0b(2)) — `hello` is the only restart signal.** A
  `/active` response whose `serverRunId` differs from the connection's is a *stale
  response*, not evidence that the run we are connected to restarted: it is discarded
  outright (its issuing request's run id must also still match). Treating it as restart
  evidence deleted the live requests of the run that was actually running, and no later
  snapshot restored them, because reconciliation only ever removes.
- **Post-Phase-4 addition** (ui-spec.md §9.1 in-flight TODO, implemented): the
  contract gains a fourth event, `request_ready` `{seq, model}` — emitted once the
  request body has been fully read (a genuine EOF on the request tee, not YARP's
  zero-length probe reads — see `RequestTeeStream`), with the model parsed from the
  already-captured request buffer off the request path (`TryWrite` fan-out like the
  others; skipped when the body has no parseable `model`). The parse itself runs on
  a dedicated, always-running consumer (`RequestModelSnifferService`, mirroring
  `CaptureChannel`/`CaptureWriterService`'s shape) rather than a fresh `Task.Run` per
  request — an integration test caught a real race where a `Task.Run`'s
  thread-pool-scheduling delay let `request_ready` arrive after `first_token` on a
  fast (warm loopback) connection; the dedicated consumer removes that variable. It
  exists so in-flight rows can show the real model within milliseconds of dispatch
  instead of after completion.

### D6 — UI structure (one screen, three regions)

```
frontend/src/
  api/types.ts        # hand-written TS mirrors of Summary/Detail/Stats/events
  api/client.ts       # fetch wrappers
  api/useEvents.ts    # EventSource hook: merges started/first_token/completed
  components/...      # shadcn-generated + app components
  App.tsx             # layout: StatsBar / RequestList / DetailPane
```

- **StatsBar** (top): total, failed, avg latency, avg tok/s, avg TTFT for the current
  session; Reset button (`POST /sessions`, confirm dialog, then refetch list+stats
  scoped to the new session — the default list view is *current session*, with an
  "all" toggle); backend names shown from `/api/status`.
- **RequestList** (left, TanStack Virtual): reverse-chron. Row: method+path (one
  line), model, status dot, duration, tok/s, tags as chips, warning-count badge
  (`warnings.length`, amber; red when `error`/status ≥ 400). In-flight rows (from
  `started`, not yet `completed`) pin to the top with a running timer (one shared
  250 ms interval, not per-row) and a subtle pulse; `first_token` shows live TTFT.
  Infinite scroll via `nextBefore`.
  **Fourth-round addition (Batch I, I0c):** SSE frames are *queued* and applied on a
  ~100 ms (10 Hz) window — one state update and one list-cache write per window instead
  of one per frame. Under a live 10,000-request burst with the tab connected, the
  per-frame version blocked the main thread for **10.3 s in a single task** while the JS
  heap climbed from 76 MB to **3.1 GB** (of a 4 GB limit) and the tab stopped responding;
  coalesced, the same burst peaks at 184 MB with a 92 ms worst task. Ordering within a
  window is preserved exactly, which is what keeps a `cleared` dividing the completions it
  deletes from the ones it does not. Imperceptible for a monitoring UI — the elapsed-time
  display already ticks on its own 250 ms interval.
- **DetailPane** (right): tabs **Overview** (all Summary metrics laid out, warnings as
  labeled badges, timing breakdown incl. `vesselOverheadMs`), **Request** / **Response**
  (pretty-printed JSON in a scrollable `<pre>`, collapse toggle, copy button; Response
  offers reassembled|raw-stream toggle for streamed rows; base64 bodies show a size
  placeholder), **Headers** (two tables; values matching the redaction shape `… / …xxxx`
  get a "redacted" pill). Empty state when nothing selected.
- Theme: Tailwind dark/light via `prefers-color-scheme`, shadcn defaults. No toggle
  this phase.
- JSON pretty-printing is `JSON.parse` + `JSON.stringify(_, null, 2)` in a `<pre>` —
  no syntax-highlight dependency this phase; unparseable text renders verbatim.

### D7 — Endpoint map ordering (updates Phase 0's `/vessel` handling)

`/vessel/api/status`, `/vessel/api/requests*`, `/vessel/api/stats`,
`/vessel/api/sessions`, `/vessel/api/events`, then `/vessel/api/{**}` → 404 JSON
(`not_found`), then `/vessel/{**}` → embedded static files / index / placeholder,
then the proxy catch-all. The Phase 0 rule stands: `/vessel/*` is never proxied,
never captured.

### D8 — API tests are backend-only this phase

Integration tests (in-proc, existing fixture style) cover the API; the React app gets
**no test harness in Phase 3** — it's four components against a tested API, verified by
the manual gate below. (Playwright, if ever, is a later decision; don't scaffold it
now.) TS types are kept honest by hand-mirroring the C# records — acceptable at this
API size; codegen is not worth the tooling.

---

## 2. New/changed layout

```
frontend/                         # D1/D6 — Vite app, dist embedded at publish
src/Vessel/
  Api/
    RequestsEndpoints.cs          # D3 list + detail
    StatsEndpoint.cs              # D3
    SessionsEndpoints.cs          # D3/D4
    EventsEndpoint.cs             # D5 SSE
    ApiJsonContext.cs             # STJ source-gen for API shapes
    StaticUi.cs                   # D1 embedded files + placeholder
  Capture/
    CaptureEvents.cs              # D5 broadcast hub
    CurrentSession.cs             # D4
  Storage/
    SqliteReadStore.cs            # D2
tests/Vessel.Tests/
  ApiTests.cs                     # list/detail/stats/sessions
  EventsTests.cs                  # SSE lifecycle
```

Changed: `CaptureRecord` (+`Seq`, `SessionId`), `CaptureContext` (seq + emits),
`CaptureChannel`/`CaptureWriterService` (writer commands, `completed` emit, session
stamping), `ProxyHandler` (`started` emit), `VesselApp` (DI + endpoint map),
`Vessel.csproj` (embedded dist + publish target), `verify/publish-smoke.ps1`
(assert `/vessel/` serves the UI when published with Node available).

---

## 3. Automated tests

| # | Assertion |
|---|---|
| U1 | List: reverse-chron, `limit` honored + capped at 500, `before` cursor pages without gap/overlap, `nextBefore` null at the end |
| U2 | Detail: decompressed bodies round-trip; UTF-8 → `text`, binary → `base64`; streamed rows expose both `responseBody` (reassembled) and `responseRaw`; unknown id → 404 `not_found` |
| U3 | Stats: totals/averages computed over a seeded mix (failed = error-or-≥400; `avgTtftMs` over streamed only; `session=current|id|all` scoping) |
| U4 | Sessions: fresh DB auto-creates session 1; `POST` creates + returns marker; rows started before a reset keep their original `session_id` even when flushed after (D4) |
| U5 | SSE: one proxied request yields `started` then `completed` with matching `seq` and a `completed.row.id` present in a subsequent list fetch; streamed request also yields `first_token` with plausible `ttftMs` |
| U6 | SSE robustness: a subscriber that never reads doesn't block the request path or other subscribers (bounded drop-oldest); disconnecting mid-stream doesn't fault the hub |
| U7 | `/vessel/api/nope` → 404 JSON with `X-Vessel-Error`; `/vessel/` without embedded dist → placeholder 200, still never proxied |
| U8 | Phase 0 T7 / Phase 1 C2 unchanged — event emission is `TryWrite`-only on the hot path |
| U9 | Full prior suite green |

## 4. Manual gate (the phase's real "done")

1. `npm run dev` + running Vessel: live rows appear as an agent runs; in-flight timer
   ticks; TTFT pops in on first token; row completes with enriched fields.
2. Click through all four tabs on: a streamed Ollama-native row, a non-streamed
   `/v1` row, an error row (dead backend), a truncated/warned row.
3. Reset session → stats bar zeroes, list scoped to new session, "all" toggle shows
   history.
4. `dotnet publish` (with Node) → single binary serves the full UI at `/vessel/`;
   publish smoke passes including the new UI check.
5. Leave the tab open through a real working session — the plan's "genuinely useful"
   bar — before ticking the phase.

## 5. Acceptance criteria (phase gate)

1. §3 tests green; full suite green (`dotnet test`, still Node-free).
2. Manual gate items 1–4 done (item 5 is the soak, as in prior phases).
3. Publish smoke passes with the embedded UI; plain `dotnet build`/`test` require no Node.
4. plan.md Phase 3 boxes ticked; deviations recorded here; architecture.md §12 updated
   if any decision here proves wrong.

## 6. Deviations from this spec

None are architectural (architecture.md §12 stands unchanged); recorded here per §1.

- **D1 static serving — no `ManifestEmbeddedFileProvider`.** `StaticUi.cs` embeds
  `frontend/dist/**` with an explicit `<LogicalName>vessel-ui/%(RecursiveDir)%(Filename)%(Extension)</LogicalName>`
  per item and builds its own `web path → manifest resource name` index at startup by
  scanning `Assembly.GetManifestResourceNames()` and normalizing `\`/`/`, instead of using
  `ManifestEmbeddedFileProvider` + `GenerateEmbeddedFilesManifest`. `frontend/` lives
  outside the project cone (`../../frontend/dist`), which makes the file provider's
  auto-derived relative-path/manifest-resource-name mapping fragile across OSes; a small
  hand-rolled lookup sidesteps it entirely with identical externally-visible behavior
  (verified end-to-end by `verify/publish-smoke.ps1`'s UI checks).
- **`ApiJsonContext` must stay one file.** Declaring `[JsonSerializable]` attributes for
  Phase 3's new API types in a second partial-class file alongside the Phase 0
  `ApiJsonContext` (as the layout in §2 suggests) trips a System.Text.Json
  source-generator bug: it emits a duplicate hint-name for shared primitive types (e.g.
  `Boolean`) reachable from JsonSerializable roots declared in different files, and the
  whole generator aborts (`CS8785`). All `[JsonSerializable]` attributes (Phase 0 + Phase
  3) now live together in `Api/ApiJsonContext.cs`; the DTO records themselves stayed
  where they were (`ErrorPayload`/`StatusPayload` in `VesselErrors.cs`,
  `CreateSessionRequest` in `ApiJsonContext.cs`). **Phase 4 note:** keep adding
  `[JsonSerializable]` attributes to this one file — don't reintroduce a second partial
  declaration with its own attributes.
- **shadcn components are hand-written, not CLI-generated.** `components/ui/{button,badge,tabs,dialog}.tsx`
  follow shadcn's visual and API conventions (Tailwind utility classes,
  `class-variance-authority` variants) but were authored by hand rather than via
  `npx shadcn add`, to avoid CLI/version coupling against a fast-moving Tailwind v4 /
  React 19 / TypeScript 6 stack pulled in fresh for this phase. They're committed
  source either way per D1 ("a code generator, not a runtime dependency"); no test or
  behavior depends on how they were produced.
- **`CurrentSession` is populated at writer startup, not lazily.** `CaptureWriterService.StartAsync`
  calls `store.EnsureInitialSession()` synchronously (after `Initialize()`, before the
  background loop starts), which — combined with the writer being registered ahead of
  Kestrel's own hosted service (existing invariant) — guarantees every request the proxy
  ever handles has a real current session id from the first byte. This was implicit in
  D4's wording ("Startup: newest sessions row... done during writer init, before
  traffic") but is called out here since U4 depends on it.
