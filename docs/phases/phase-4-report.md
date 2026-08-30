# Phase 4 — Implementation Report

> Status: **implementation complete; acceptance was not yet met at the time of writing.**
> (Code review finding D04: this report previously said "complete" outright, but its own
> final paragraph — and the acceptance table below — already listed the 10k-row soak and
> the truncated-response litmus test as outstanding; those are acceptance criteria, not
> optional extras, so this phase was not actually accepted at that point. A subsequent
> external code review (see [code-review-phase-4.md](code-review-phase-4.md)) found
> further defects across this implementation; the full remediation is tracked in
> [code-review-phase-4-plan.md](code-review-phase-4-plan.md), whose batches have since
> landed. This report is left as the historical implementation record of the original
> Phase 4 work — it is not the current acceptance status of the tree.
> Spec: [phase-4.md](phase-4.md) · Plan: [plan.md](plan.md) · Design authority: [architecture.md](architecture.md)

## What was built

Per the spec's §2 layout: list filters + facets (D1/D2), a writer-thread clear command
(D6), the config editor's live-apply model (D7 — the phase's real design work), and the
frontend's filter bar, rendered message view, Overview additions, and Data/Config panels.
No schema migration, as the spec predicted.

### Backend

| Piece | File | Notes |
|---|---|---|
| C1 fix | `Api/RequestsEndpoints.cs` | `Detail` uses `long.TryParse` on the route id → existing `not_found` 404 path instead of an unhandled `FormatException` |
| List filters | `Storage/SqliteReadStore.cs` (`ListRequests`) | `q` (FTS5, sanitized — every token quoted, embedded quotes doubled, so `AND`/`(`/`*` are always literal), `backend` (case-insensitive), `model`/`format` (exact), `tag` (exact element match via `json_each(COALESCE(tags,'[]'))`, never substring), `status` (`ok`\|`error`, mirrors `GetStats`'s `failed` expression), `warned`. `requests_fts` is joined only when `q` is present — an unconditional join would silently drop rows with no prompt/response text (they never get an FTS row) |
| Facets | `Storage/SqliteReadStore.cs` (`GetFacets`), `Api/FacetsEndpoint.cs` | Distinct backend/model/format/tag values, session-scoped, capped 100, alphabetical, no counts |
| Clear command | `Capture/CaptureWork.cs` (`ClearCommand`), `Capture/CaptureWriterService.cs` (`RunClear`), `Storage/SqliteCaptureStore.cs` (`Clear`), `Storage/ICaptureStore.cs`, `Api/RequestsEndpoints.cs` (`Delete`) | `DELETE /requests?scope=all\|before=` runs on the writer thread — same shape as `CreateSessionCommand`/`DeleteOldest`: FTS rows deleted by rowid-subquery, then `requests`, one transaction, `incremental_vacuum` after commit |
| Config live-apply | `Config/ConfigStore.cs` (new), `Config/ConfigLoader.cs` (`Validate` made public + case-insensitive duplicate-backend-name check), `Api/ConfigEndpoints.cs` (new) | `ConfigStore` owns the current `VesselConfig` behind `Volatile.Read` + a version counter; `Apply` validates with the same rules as startup, persists, swaps, bumps version, under a lock |
| Live consumers | `Proxy/BackendRegistry.cs`, `Proxy/ProxyHandler.cs`, `Formats/FormatEnricher.cs`, `Storage/SqliteCaptureStore.cs` | `BackendRegistry`/`FormatEnricher` rebuild derived state lazily on version change (two-constructor split — `FormatEnricher`/`SqliteCaptureStore` keep their static-`VesselConfig` constructors for tests, add a `ConfigStore`-backed one for the live app); `ProxyHandler` reads `Capture.MaxBodyMb`/`Timeouts.ActivitySeconds` fresh per request instead of caching them at construction |
| Wiring | `VesselApp.cs` (`Build` gains a `configPath` param), `Program.cs`, new routes (`facets`, `DELETE /requests`, `GET`/`PUT /config`) | `builder.Services.AddSingleton(config)` (raw `VesselConfig`) removed — every live consumer now depends on `ConfigStore` |
| JSON contexts | `Api/ApiJsonContext.cs` | `FacetsResponse`, `ClearResponse`, `Vessel.Config.ConfigApplyResult` added to the single existing partial-class file, per the Phase 3 finding (splitting trips a source-gen hintName collision) |

### Frontend (`frontend/`)

| Piece | File(s) | Notes |
|---|---|---|
| C2 fix | `api/useEvents.ts` | Tracks "seen one `open` before" in a ref; a subsequent `open` (post-reconnect) invalidates `['requests']`/`['stats']` |
| Filter bar | `components/FilterBar.tsx`, `api/{types,client}.ts` | Debounced (300 ms) search, backend/model/format dropdowns from facets (hidden when ≤1 value), tag chip picker, status toggle, warnings-only toggle, active-filter chips + clear-all |
| List wiring | `components/RequestList.tsx` | `queryKey` includes the filter object; live `completed` inserts are suppressed while any filter beyond session scope is active, replaced by a "N new — refresh" pill |
| Rendered view | `render/{types,openai,anthropic,ollama,index}.ts`, `components/{MessageView,ToolCallCard}.tsx` | Per-format, per-body (request/response separately, matching `DetailPane`'s existing tabs) extractors to a normalized view model; markdown via `react-markdown`+`remark-gfm` (the one new dependency); thinking collapsed by default; text >4000 chars clamps with an expand control; tool calls as collapsible cards |
| Detail pane | `components/DetailPane.tsx` | Request/Response tabs default to the rendered view when extraction succeeds, with a "Rendered \| Raw JSON" toggle back to the untouched Phase 3 `PrettyJson` view (kept on every tab, every format); Overview gets a rate-limit table (client-side header scan) and an Anthropic cached-tokens note |
| Data/Config panels | `components/{DataPanel,ConfigPanel}.tsx`, `components/ui/dialog.tsx` (new generic `Dialog`) | Gear icon on `StatsBar` opens a tabbed modal; Data = Clear all/before, each behind typed-confirmation ("DELETE"); Config = backends table (add/remove/default/type/injectStreamUsage), retention/capture/warnings numbers, listen (with a static restart note), client-side required-field checks, server 400 message surfaced verbatim |

## Verification results

### Automated tests — 167/167 pass at the time of this report (`dotnet test`; 142 prior + 25 new)

**Note (code review D04):** this count describes the tree as it stood when Phase 4 was
first implemented. It does not describe the current tree — later work (the OpenAI
Responses adapter, `request_ready`/in-flight-detail additions, and the code-review
remediation batches) added substantially more tests and changed some of the ones listed
below; run `dotnet test` for the current total rather than relying on this number.

| # | Coverage | Where |
|---|---|---|
| V1/C1 | `/requests/abc` and `/requests/99999999999999999999` → 404 `not_found`, not 500 | `ApiTests.Detail_NonNumericOrOverflowingId_404NotFound` |
| V2/V3 | Each filter alone (backend case-insensitivity, model/format exact, tag exact-element, status ok/error, warned), the three-way `backend`+`status=error`+`warned=1` combination (proven a strict AND via a decoy matching two of three), FTS matching prompt and response text separately, hostile FTS input never erroring, FTS composing with cursor paging (no gap/overlap) | `FilterTests.cs` (11 tests) |
| V4 | Facets scoped, distinct, capped, alphabetical | `FilterTests.Facets_ScopedDistinctCappedAlphabetical` |
| V5 | Clear-before leaves newer rows / removes older, `{deleted}` accurate, no orphaned FTS rows; clear-all empties everything and the file shrinks (vacuum ran); missing `scope`/`before` → 400 | `ClearTests.cs` (3 tests) |
| V6 | Invalid PUT (duplicate backend name case-insensitive, bad URL, non-positive retention, unknown default backend) → 400 + nothing persisted + GET unchanged; valid PUT rewrites the file with unknown properties preserved | `ConfigApplyTests.cs` (5 tests) |
| V7 | A backend `baseUrl` PUT lands the very next request on the new target; retention tightening applies on the next writer batch; `listen` PUT reports `restartRequired: ["listen"]` and the original listener keeps serving; a non-`listen` PUT reports no restart required | `ConfigApplyTests.cs` (4 tests) |
| V8 | A request already in flight when a config PUT repoints its backend completes normally (`RouteDecision` resolved once, at request start) | `ConfigApplyTests.InFlightRequest_UnaffectedByConcurrentConfigPut` |
| V9 | Full prior suite green (142/142 carried, 0 regressions) | — |

`StubBackend.cs`'s `/api/chat` gained an optional `?model=` override (defaulting to
`"stub-model"` for every existing caller) so `FilterTests` could seed distinct `model`
values cheaply.

### Manual gate — driven through the actual embedded UI (not the Vite dev proxy) against real local Ollama traffic

Seeded a mix of real traffic through a locally running Vessel (non-streamed and streamed
`ollama-chat`, `openai-chat`, a tagged truncated response, a tool-call exchange, and an
unknown-backend 404) and drove the built SPA in a browser:

1. **Filter bar**: backend dropdown (case-insensitive `BETA`→`beta`-style match verified
   in tests; UI dropdown populated from real facets), tag chips filtered to exactly the
   tagged row, status=error surfaced both the unknown-backend and a real proxy failure,
   free-text search (`Boston`) found the tool-call exchange by prompt content — all with
   the active-filter chips + "Clear filters" rendering correctly.
2. **Rendered message view**: the tool-call exchange showed a collapsible `🔧 get_weather`
   card with pretty-printed args on the Response tab and the user's markdown-rendered
   prompt on the Request tab; Ollama's metrics (`eval_count`, `load_duration`, etc.)
   rendered as a collapsed Params section; the "Rendered ⇄ Raw JSON" toggle worked on both
   tabs.
3. **Warning badges**: a `num_predict`-capped request showed both "Truncated response" and
   "Cold model load" badges on Overview, matching its `stop_reason: length`.
4. **Config live-apply**: `GET /config` populated the real editor (backend name, baseUrl,
   type, retention/capture/warnings numbers, listen); a retention change PUT persisted to
   the real `vessel.json` on disk and was reflected on the next `GET` — confirmed by
   reading the file directly, not just trusting the UI.
5. **Data panel guardrail**: "Clear all…" reveals a typed-confirmation input; verified via
   direct DOM inspection that "Confirm delete" stays `disabled` until "DELETE" is typed —
   cancelled rather than executed, to preserve the seeded data for further checks.
6. **C2 (SSE reconnect)**: killed and restarted the Vessel process with the tab open
   (against the embedded UI directly, not through the Vite dev proxy, which was found
   during this check to not propagate an upstream connection failure to the browser at
   all — a dev-only proxy limitation, not a product concern). Network-level evidence
   confirmed the full cycle: `ERR_CONNECTION_RESET` → two `ERR_CONNECTION_REFUSED` retries
   → a successful reconnect — immediately followed by fresh `GET /requests` and `GET
   /stats` calls, exactly matching the reconnect-triggered invalidation C2 adds. A
   request sent shortly after the reconnect didn't appear via the ordinary live SSE
   push in this manual run — root-caused as a genuine race (not a Phase-3-only concern,
   see Deviations #6) and fixed in `RequestList.tsx`.

### Publish smoke (win-x64, self-contained, single file)

| Configuration | Size | Result |
|---|---|---|
| Untrimmed (shipping) | 102.5 MB | all checks pass — first-run config creation, `/vessel/api/status`, proxying, unknown-backend 404, embedded SPA shell + bundled JS asset |

**Correction (code review R01).** The paragraph above is wrong about the script:
`verify/publish-smoke.ps1` does not have its own separate `npm ci && npm run build`
step — it invokes `dotnet publish` directly, which is exactly why R01's clean-publish
defect (the frontend build running too late in the MSBuild target graph, so a truly
clean tree published with zero embedded UI resources) was able to pass this smoke check
silently for as long as it did: an existing `frontend/dist` from any prior `npm run
build` masked the broken ordering. The new `react-markdown`/`remark-gfm` dependencies
did install and build cleanly from a fresh `node_modules` in this run, but that was
incidental to whatever triggered the frontend build at the time, not evidence this
script independently verified it. Batch A's fix (see
[code-review-phase-4-plan.md](code-review-phase-4-plan.md)) corrects the build ordering
and extends this script to publish from a tracked-files-only copy with no pre-existing
`dist`, closing the gap this correction describes.

## Deviations and findings

1. **Route-constraint fallthrough already caught the C1 cases** — `/vessel/api/requests/{id:long}`'s
   `:long` constraint rejects non-numeric and overflowing ids before `Detail` ever runs,
   falling through to the `/vessel/api/{**rest}` 404 catch-all. The `TryParse` swap in
   `Detail` is still applied, defensively, per the spec's wording, and the V1 regression
   test now guards the behavior explicitly either way.
2. **`FormatEnricher`/`SqliteCaptureStore` got a second constructor rather than a
   signature change** — both are constructed directly (not via DI) in several existing
   test files. Rather than touching every call site, each keeps its original
   `VesselConfig`-based constructor (static snapshot, used by tests, byte-for-byte
   unchanged behavior) and gains a new `ConfigStore`-based one (live, version-aware) used
   only by `VesselApp`'s DI registration. `BackendRegistry`/`ProxyHandler` had no direct
   test constructors, so those were changed in place.
3. **A case-insensitive duplicate-backend-name check was added to `ConfigLoader.Validate`**
   (shared by startup and `PUT`) — not previously needed, since a hand-authored startup
   config is unlikely to have two backends differing only in case, but a `PUT` payload can
   (JSON object keys are case-sensitive, so `"ollama"` and `"Ollama"` survive
   deserialization as two distinct dictionary entries, which would silently collide in
   `BackendRegistry`'s case-insensitive lookup undetected).
4. **`VesselApp.Build` gained a third parameter (`configPath`)** — not anticipated by the
   spec's layout, but required once `ConfigStore` needed to know where to persist `PUT`s;
   `Program.cs` already computed this value, it just wasn't threaded through. `VesselFixture`/`TestVessel`
   both gained a `ConfigPath` (a file that need not exist beforehand — `ConfigStore` only
   reads its in-memory snapshot on `GET` and touches disk on `Apply`).
5. **Extractors split per-body, not per-exchange** — the spec's D4 wording reads naturally
   as one `RenderedView` per request/response pair, but `DetailPane` has always had
   separate Request and Response tabs, each with its own independent raw-JSON toggle
   (streamed rows additionally toggle reassembled-vs-raw-stream on the Response tab
   alone). Matching that existing structure meant each format's extractor is really two
   functions (`extract{Format}Request`/`extract{Format}Response`), not one — kept the UI
   change additive (a view-mode toggle layered on top of the untouched raw view) rather
   than restructuring the tabs.
6. **Live-splice/refetch race in `RequestList.tsx`, found post-verification** — the
   Manual gate's C2 check surfaced a request that completed shortly after a reconnect not
   appearing live. Root cause (flagged in review, not something the manual gate's
   network-log evidence alone would have caught): the `completed` handler splices the new
   row into the query cache via `setQueryData` regardless of whether a refetch is
   currently in flight for that same key. If one is — and C2's reconnect invalidation
   guarantees exactly that — the refetch's snapshot was taken from the DB *before* the
   writer inserted the row, so when it resolves it overwrites the cache and the spliced
   row is gone until some later refetch. Not reconnect-specific: any refetch overlapping
   a completion (a filter clear, a scope toggle) can eat a row the same way. Fixed by
   checking `queryClient.getQueryState(queryKey)?.fetchStatus === 'fetching'` before
   splicing; if a fetch is in flight, `invalidateQueries` instead — since `completed` only
   fires after the writer's insert, any refetch that starts after this point is
   guaranteed to include the row, and invalidating queues exactly such a refetch behind
   the in-flight one.

   **Correction (code review R10).** The last sentence above is wrong, and the fix it
   describes was insufficient. `invalidateQueries` does *not* reliably queue a fresh
   refetch behind an in-flight one: for an **initial** fetch — no cached data yet — this
   TanStack Query version reuses the existing promise, so the invalidation is absorbed and
   the completion is lost with no later refetch to recover it. The review reproduced it
   directly (one query call, zero rows, `isInvalidated: false`). Completions arriving
   while any list fetch is unsettled are now **buffered and merged after settlement**
   (`useLiveHistory`), which is correct for the initial-load case as well as the
   overlapping-refetch case; the merge dedupes by id, so it is harmless when the settling
   fetch already contained the row.

## Acceptance criteria status (as of this report — see the correction below)

| # | Criterion | Status |
|---|---|---|
| 1 | §4 tests green; full suite green; frontend `tsc` clean | ✅ 167/167 backend; `tsc -b && vite build` clean |
| 2 | Manual gate 1–5 done | ✅ filter bar, rendered view + warnings, config live-apply, data-panel guardrail, and C2 all driven live against real Ollama traffic through the actual embedded UI |
| 3 | Publish smoke passes; architecture.md §9 updated with the live-apply model | ✅ `verify/publish-smoke.ps1` passes (102.5 MB, untrimmed win-x64); architecture.md §9.1 added — **but see the R01 correction above: this smoke check could not have caught a broken clean publish at the time** |
| 4 | plan.md Phase 4 boxes ticked; deviations recorded here | ✅ |
| 5 | 10k-row soak; truncated-response litmus test (two ways, under ten seconds) | ❌ **not done** — explicitly deferred as "the remaining human-at-the-keyboard items" below, but these are acceptance criteria from Phase 4 §5, not optional follow-ups (code review D04) |

**Correction (code review D04): this phase was not actually accepted at the time of
this report.** Row 5 above is a genuine acceptance gap, not a footnote — Phase 4 §5
requires both the soak and the litmus test for acceptance. The original text below
undersold that by calling them "remaining human-at-the-keyboard items, as in prior
phases," implying routine follow-up rather than an open acceptance criterion. Both were
subsequently exercised as part of the code-review remediation's final gate (see
[code-review-phase-4-plan.md](code-review-phase-4-plan.md)); this report is left as the
original point-in-time record and is not updated with those results here.

A 10k-row soak and the plan's "truncated response from this morning, two ways, under ten
seconds" litmus test are the remaining human-at-the-keyboard items, as in prior phases.
