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
- `GET /stats?session={id|current|all}` (default `current`) →
  `{ total, failed, avgDurationMs, avgTokPerSec, avgTtftMs, sessionId, sessionStartedAt }`.
  `failed` = `error` set or status ≥ 400. Averages over non-null values only;
  `avgTtftMs` over streamed rows.
- `GET /sessions` → newest-first list; `POST /sessions` → creates a marker (optional
  `{name}`), returns it. No delete.
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
- Events (named SSE events, JSON data): `started` `{seq, startedAt, method, path,
  backend, tags}` — emitted at handler entry; `first_token` `{seq, ttftMs}` — emitted
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
