# Phase 3 — Implementation Report

> Status: **complete**. The plan's "genuinely useful" soak (leaving a tab open through a
> real working session) is the remaining human-at-the-keyboard item, as in prior phases.
> Spec: [phase-3.md](phase-3.md) · Plan: [plan.md](plan.md) · Design authority: [architecture.md](architecture.md)

## What was built

The UI API, SSE lifecycle feed, sessions, and the embedded React frontend, per the
spec's §2 layout. No schema migration — `sessions` and `requests.session_id` already
existed; this phase starts populating them (D4).

### Backend

| Piece | File | Notes |
|---|---|---|
| Sessions | `src/Vessel/Capture/CurrentSession.cs` | D4: process-wide "active session" id, set at writer startup before Kestrel accepts traffic, and again after every reset |
| Writer commands | `src/Vessel/Capture/CaptureWork.cs` | D4: the channel's union type — `CapturedRequest` \| `CreateSessionCommand`, so `POST /sessions` never opens a second write connection |
| Session insert | `src/Vessel/Storage/SqliteCaptureStore.cs` | `EnsureInitialSession()` (creates "session 1" on a fresh DB) + `CreateSession(name)`, both writer-thread-only |
| `CaptureRecord`/`CaptureContext` | `src/Vessel/Capture/CaptureRecord.cs`, `CaptureContext.cs` | + `Seq` (process-lifetime SSE correlation id) and `SessionId` (captured once, at request start — D4's "keeps the session it started in") |
| SSE hub | `src/Vessel/Capture/CaptureEvents.cs` | D5: per-subscriber bounded (256) drop-oldest channel; `started`/`first_token`/`completed`; no-subscriber publish is a single emptiness check |
| SSE emit points | `ProxyHandler.cs` (`started`), `ResponseTeeStream.cs` (`first_token`, on the first streamed byte), `CaptureWriterService.cs` (`completed`, after insert — real DB id, `row: null` on a dropped batch) | Request-path emit is `TryWrite`-only (D5/U8) |
| SSE endpoint | `src/Vessel/Api/EventsEndpoint.cs` | Hand-rolled `event:`/`data:` frames, 15 s `: ping` heartbeat, clean unsubscribe on disconnect |
| Read store | `src/Vessel/Storage/SqliteReadStore.cs` | D2: separate `Mode=ReadOnly` pooled connections, never the writer's; list/detail/stats/sessions queries, all indexed |
| Row/DTO shapes | `src/Vessel/Storage/Summary.cs` | `Summary`, `RequestListResponse`, `RequestDetail` (flat, not nested), `BodyPayload` (text vs. base64), `StatsResponse`, `SessionInfo` |
| REST endpoints | `src/Vessel/Api/{RequestsEndpoints,StatsEndpoint,SessionsEndpoints}.cs` | D3: cursor list (`limit`/`before`/`session`), detail (404 `not_found`), stats (`current`\|`all`\|id), sessions list/create |
| JSON source-gen | `src/Vessel/Api/ApiJsonContext.cs` | All API `[JsonSerializable]` types in one partial-class file (see Deviations) |
| Embedded UI | `src/Vessel/Api/StaticUi.cs` | D1: serves `frontend/dist` from embedded resources, or a placeholder page when none is embedded |
| Routing | `src/Vessel/VesselApp.cs` | D7: `/vessel/api/*` routes → `/vessel/api/{**}` 404 JSON → `/vessel/{**}` static/placeholder → proxy catch-all |
| Publish pipeline | `src/Vessel/Vessel.csproj` | `frontend/dist/**` embedded under a `vessel-ui/` logical-name prefix; `BuildFrontend` target (`npm ci && npm run build`) runs `BeforeTargets="PrepareForPublish"` — verified to run before the publish-specific compile even from a clean clone |

### Frontend (`frontend/`, new)

Vite + React 19 + TypeScript 6 + Tailwind v4, TanStack Query + Virtual, hand-written
shadcn-style primitives (committed, not a runtime dependency — same property as
CLI-generated files).

| Piece | File(s) | Notes |
|---|---|---|
| API types + client | `src/api/types.ts`, `client.ts` | Hand-mirrored TS types for every wire shape; typed `fetch` wrappers |
| SSE hook | `src/api/useEvents.ts` | Tracks in-flight requests by `seq` (`started` → `first_token` → removed on `completed`); `useNowTick` drives one shared 250 ms timer for every in-flight row |
| Stats bar | `src/components/StatsBar.tsx` | Totals/failed/avg latency/tok-s/TTFT, Current/All toggle, Reset (confirm dialog), backend health dots |
| History list | `src/components/RequestList.tsx`, `RequestRow.tsx` | `useInfiniteQuery` + `useVirtualizer`; live `completed` rows are spliced into the first page's cache (dedup by id — REST wins on a race), not a refetch |
| Detail pane | `src/components/DetailPane.tsx`, `PrettyJson.tsx` | Overview/Request/Response/Headers tabs; `JSON.parse`+`stringify(_,null,2)` in a `<pre>`; reassembled↔raw-stream toggle for streamed rows; redacted-header pills (`…` marker) |
| UI primitives | `src/components/ui/{button,badge,tabs,dialog}.tsx` | shadcn-style, hand-written (see Deviations) |
| Shell | `src/App.tsx`, `main.tsx` | StatsBar / RequestList / DetailPane, no router (D6) |

## Verification results

### Automated tests — 142/142 pass (`dotnet test`; 135 prior + 7 new)

| # | Coverage | Where |
|---|---|---|
| U1 | Reverse-chron list; `limit` capped at 500; `before` cursor pages with no gap/overlap; `nextBefore` null at the end | `ApiTests.List_Pagination_ReverseChron_NoGapOrOverlap_LimitCapped` |
| U2 | Detail bodies decompress + classify (UTF-8 → text, binary → base64); a streamed recognized-format row exposes both `responseBody` and `responseRaw`; unknown id → 404 `not_found` | `ApiTests.Detail_BodiesDecompressAndClassify_StreamedExposesBothBodies_UnknownId404` |
| U3 | Totals/averages over a seeded mix, cross-checked against `CaptureDb` ground truth; `avgTtftMs` streamed-only; `session=current\|id\|all` scoping | `ApiTests.Stats_TotalsAndAverages_SessionScoping` |
| U4 | Fresh DB auto-creates "session 1"; `POST` creates + returns a marker; a request started before a reset keeps its original `session_id` after flushing post-reset | `ApiTests.Sessions_FreshDbAutoCreatesSessionOne_PostCreatesMarker_InFlightRequestKeepsOriginalSession` |
| U5 | One streamed request → `started` → `first_token` (plausible `ttftMs`) → `completed`, matching `seq`; `completed.row.id` present in a subsequent list fetch | `EventsTests.Sse_StartedFirstTokenCompleted_MatchingSeq_RowInListAfterward` |
| U6 | A subscriber that never reads (256-capacity, drop-oldest) never slows a 300-request flood; disconnecting it mid-stream doesn't fault the hub — a second subscriber keeps working | `EventsTests.Sse_SlowSubscriberNeverBlocks_DisconnectDoesNotFaultHub` |
| U7 | `/vessel/api/nope` → 404 JSON + `X-Vessel-Error`; `/vessel/` never proxies (no backend content leaks) | `ApiTests.UnknownApiPath_404Json_UiPathNeverProxied` |
| U8 | Event emission stays `TryWrite`-only on the hot path | Phase 0 `T7` / Phase 1 `C2` still green, unchanged |
| U9 | Full prior suite green | 135/135 carried, 0 regressions |

`StubBackend` gained a `?stream=1` flag on `/api/chat` (wire-true to the
`ollama-chat/streamed-basic` golden fixture) so U2 could exercise a streamed,
format-recognized row without a live model.

### Manual gate — all four items done against a real local Ollama

Driven through the actual Vite dev server in a browser (not just `curl`/xunit):

1. **Live rows + in-flight timer + TTFT**: sent non-streamed, streamed, truncated
   (`num_predict`-capped), and dead-backend requests through Vessel while the tab was
   open — rows appeared with no manual refresh, the in-flight row's elapsed timer ticked
   (observed mid-flight: `1.75s` → completed at `3.27s`/`163.1 tok/s`), and completed
   rows carried the enriched fields.
2. **All four detail tabs**, across a streamed Ollama-native row, a non-streamed
   `/v1/chat/completions` row, a dead-backend error row, and a `stop_reason: length`
   truncated row: Overview (metrics, `Truncated response`/`Proxy error` warning badges,
   timing, tokens), Request/Response (pretty-printed JSON, Collapse/Copy), Response's
   reassembled↔raw-stream toggle (confirmed the raw view showing individual NDJSON
   chunks vs. the folded message), Headers (request + response, redaction pill visible).
3. **Reset session**: confirm dialog → stats bar zeroed, list scoped to the new
   (empty) session; "All" toggle showed the 5 pre-reset rows with correctly aggregated
   stats (`total: 5, failed: 2`); a post-reset request landed live in the new session.
4. **`dotnet publish`**: verified twice from a fully clean `bin`/`obj`/`frontend/dist`
   state that the frontend-build-then-compile ordering is real (not a stale-hash
   coincidence) — `BuildFrontend`'s `BeforeTargets="PrepareForPublish"` runs before the
   publish-specific compile/embed step, even though an *inner* build happens first with
   nothing embedded yet. `verify/publish-smoke.ps1` (extended this phase) passes,
   including the new UI checks.

### Publish smoke (win-x64, self-contained, single file)

| Configuration | Size | Result |
|---|---|---|
| Untrimmed (shipping) | 102.3 MB | all checks pass, incl. `/vessel/` serving the embedded SPA shell and its bundled JS asset loading |

Trimming stays deferred to Phase 6, unchanged from phases 1–2.

## Deviations and findings

Recorded in place in [phase-3.md](phase-3.md) §6; summary:

1. **`ApiJsonContext` must stay one file.** Declaring `[JsonSerializable]` for this
   phase's new types in a second partial-class file (as the spec's §2 layout suggests)
   trips a System.Text.Json source-generator bug — a duplicate hint-name for shared
   primitives (e.g. `Boolean`) reachable from roots declared in different files, aborting
   the whole generator (`CS8785`). Caught immediately at first build. Fix: every
   `[JsonSerializable]` attribute (Phase 0 + Phase 3) now lives in one file,
   `Api/ApiJsonContext.cs`. Flagged for Phase 4, which will add more.
2. **No `ManifestEmbeddedFileProvider`.** `frontend/` lives outside the project cone
   (`../../frontend/dist`), which makes the file provider's auto-derived
   manifest-resource-name mapping fragile across OSes. `StaticUi.cs` instead embeds with
   an explicit `vessel-ui/` `<LogicalName>` prefix and builds its own
   `web path → resource name` index at startup, normalizing `\`/`/` itself — verified
   end-to-end by the publish smoke's UI checks rather than trusted blindly.
3. **shadcn components hand-written, not CLI-generated.** Same "committed code
   generator output" property either way (D1); avoids CLI/version coupling against a
   freshly-pulled Tailwind v4 / React 19 / TypeScript 6 stack. No test or behavior
   depends on how they were produced.
4. **`CurrentSession` populated at writer startup, not lazily** — `CaptureWriterService.StartAsync`
   calls `EnsureInitialSession()` before the background loop starts, so every request
   the proxy ever handles has a real session id from the first byte. Implicit in D4's
   wording; called out explicitly since U4 depends on it.
5. **`BodyPayload` needed `JsonIgnore(WhenWritingNull)` on both fields** — caught by U2:
   without it, STJ serializes every property regardless of null, so `{text, base64}`
   both appeared instead of exactly one.
6. **`POST /sessions` reads its body without a `Content-Length` gate** — caught by U4:
   gating on `ContentLength > 0` silently dropped a JSON body in cases where the client
   didn't declare one up front. Any parse failure (missing body, malformed JSON) now
   just falls back to "no name" instead of erroring.

## Acceptance criteria status

| # | Criterion | Status |
|---|---|---|
| 1 | §3 tests green; full suite green (`dotnet test`, still Node-free) | ✅ 142/142 |
| 2 | Manual gate items 1–4 done (item 5 is the soak) | ✅ all four verified live against real Ollama traffic |
| 3 | Publish smoke passes with the embedded UI; plain `dotnet build`/`test` require no Node | ✅ (confirmed `frontend/dist` absent → `dotnet test` still 142/142, no npm invoked) |
| 4 | plan.md Phase 3 boxes ticked; deviations recorded; architecture.md §12 updated if needed | ✅ ticked; deviations in phase-3.md §6; none architectural, §12 unchanged |

A real working session with the tab left open (the plan's "genuinely useful" bar) is the
remaining human-at-the-keyboard item, as in phases 0–2.
