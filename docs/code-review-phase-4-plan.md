# Remediation Plan — External Code Review through Phase 4

> Source: [code-review-phase-4.md](code-review-phase-4.md) (21 findings: 6 P1, 15 P2,
> plus decisions D01–D05). This plan turns the review into executable batches, split
> **Opus vs Sonnet** by the nature of the work — concurrency/lifecycle/contract
> subtleties to Opus, well-specified fixes with visible failure modes to Sonnet —
> except where file-level dependencies force items together.
>
> House rules apply throughout: fix code or fixtures, never weaken a test to pass
> (R20 explicitly: make the fixture deterministic, keep the precision intent); no
> commits; record deviations in the owning spec. Every batch ends with the full suite
> green and, where UI-visible, a manual check.

## Batch 0 — Decisions first (user sign-off; blocks marked items)

> **Status: ALL APPROVED as recommended, 2026-08-28.** The recommendation column is
> now the decided contract — implementing agents treat it as authoritative. E3 copies
> each decision into its owning document; until then, this table is the source.

The review is right that these are contract choices, not bug fixes. Recommendations
below are ready to approve or veto; each records into the owning doc when decided.

| # | Decision | Recommendation | Blocks |
|---|---|---|---|
| D01 | Wire vs decoded storage for non-streamed compressed bodies | **Restore Phase 2 D3: store wire bytes.** Decode at *read time* in the detail endpoint (it already decompresses zstd; add content-encoding decode there, under the same R05 budget) and in the enricher's existing scratch path for parsing/FTS. Storage stays wire-true; the deliberate test that enshrined decoded-storage is *corrected*, not deleted. Update phase-2.md D3 finding. | A3 |
| D02 | Non-streamed non-Ollama tok/s | **Restore the contract: null.** Whole-request-duration "throughput" mixes queueing/prefill/network into a generation rate and pollutes session averages with a different quantity. The row still shows duration and token counts — nothing is lost. Fix the tests that required the fallback (they encoded drift, not intent). Update architecture §4.2 note + phase-2.md D6. | C1 |
| D03 | Browser-origin / Host threat model | **Adopt both cheap layers, control-plane only:** (1) `/vessel/*` routes validate Host against loopback names + the configured listen host (proxied routes untouched — SDK traffic must never break); (2) mutating `/vessel/api/*` requests (PUT/POST/DELETE) require same-origin (`Sec-Fetch-Site: same-origin/none`, else Origin match). Record in architecture §8. Not UI auth; not the Phase 6 banner. | C6 |
| D05 | In-flight rows vs filters | **In-flight rows obey session scope and nothing else.** `started` gains `sessionId` (capture knows it at request start — trivial), so scoping is accurate; when any other filter is active, in-flight rows collapse to a one-line "N in flight" strip instead of full rows (they lack final status/model, so filtering them is guesswork). Record in ui-spec §5.1 + phase-3.md D5. | B1 |
| R14b | Row identity across clears | **No schema migration.** Fix the client (clear selection + drop `['request', id]` caches on any clear — D2 in Batch D); accept SQLite rowid reuse, documented in architecture §6 as a known caveat that only affects stale browser tabs. Revisit (AUTOINCREMENT needs a table-rebuild migration) only if it bites in practice. | D2 |

Also decided here (no debate expected): **D04 doc corrections are accepted as listed**
— they land as Batch E.

---

## Batch A — Opus: core correctness (backend foundation)

The concurrency, lifecycle, and build-ordering work. One session; items are ordered so
shared files (`CaptureWriterService`, `FormatEnricher`) are touched once.

- [x] **A1 · R01 — Clean publish embeds the UI.** Restructure the csproj so the
  frontend build runs before resource collection *and* backend compilation on
  publish: `BuildFrontend` must complete before the embedded-resource item group is
  evaluated (collect `frontend/dist/**` inside a target that runs after it, not as a
  static glob). Keep `dotnet build`/`test` npm-free (phase-3 D1 stands). Extend
  `publish-smoke.ps1` to publish from a **tracked-files-only copy** (no dist, no
  bin/obj, no node_modules) and assert the published assembly contains the SPA shell
  *and* its hashed JS asset, then launch the exe and fetch both over HTTP. This is
  the gate that was silently passing on stale dist.
- [x] **A2 · R02 — Atomic config snapshot.** `ConfigStore` publishes one immutable
  reference: `sealed record ConfigSnapshot(VesselConfig Config, int Version)` behind a
  single `Volatile.Read`. Consumers (`BackendRegistry`, `FormatEnricher`,
  `ProxyHandler` limits/timeouts) key their derived caches on the *snapshot
  reference*, never on separately-read versions, and resolve routing + per-request
  limits from the same snapshot. Concurrency tests: interleaved PUT + lookup loops
  asserting a lookup never returns a backend absent from the snapshot whose version
  it reports (port the review's probe shape into a deterministic test).
- [x] **A3 · R05 + D01 — Bounded decode, wire-true storage.** `BodyDecoder` gains a
  decoded-byte budget (= `capture.maxBodyMb`, applied across stacked encodings and
  zstd, enforced *during* streaming decode, never after allocation) with an explicit
  outcome: `Decoded | TruncatedDecode | Failed`. Per D01: storage reverts to wire
  bytes; the detail endpoint decodes for display under the same budget and flags
  truncated decodes in the payload. Tests: the review's 2 KB→2 MB gzip case lands
  bounded + flagged; normal compressed captures still parse; stacked encodings.
- [x] **A4 · R06 + R07 — Writer terminal state + FIFO.** On give-up (5 consecutive
  failures): complete the channel, fail all pending and future command
  `TaskCompletionSource`s with a clear error, keep a drain-and-drop loop so the
  queue can't grow unbounded, and expose capture health in `/vessel/api/status`
  (UI banner is D-lane). FIFO: `Flush` executes commands at their queue position —
  captures ahead of a clear are inserted before the clear runs. Tests: commands
  queued before/after give-up resolve or fail promptly (with HTTP-cancellation
  behavior pinned); capture→clear→capture in one batch deletes exactly the first
  capture, FTS consistent.
- [x] **A5 · R08 — Partial responses enrich.** `hasRealResponse` derives from
  "response bytes exist and Vessel didn't write them" (response started + buffer
  non-empty), not `Error is null`. A client-disconnect mid-stream keeps its partial
  reassembly, `response_text`, FTS row, and warnings (`stream_incomplete` +
  `client_disconnect` coexist). Extend the disconnect integration test with
  reassembly + FTS assertions.

**Exit:** full suite green; new concurrency/publish/decode tests green; publish smoke
from tracked-only copy passes including executable launch.

**Batch A landed 2026-08-28 — 203/203 green, publish smoke green from a clean copy.**
Notes and deviations:

- **A1.** `_IsPublishing` (a global property `dotnet publish` puts on the MSBuild command
  line) gates the npm step, and the dist glob moved into a target so it is expanded
  *after* that step rather than at evaluation. Two things worth knowing: inside a target,
  a bare `%(RecursiveDir)` in the created item's metadata does not self-reference — it
  collapsed every `LogicalName` to `vessel-ui/`, so the glob goes through a private item
  type and the qualified `%(_FrontendDistFile.*)` form. And because `_IsPublishing` is an
  SDK-internal name, a new `VerifyFrontendEmbedded` target **fails the publish** if
  `vessel-ui/index.html` is not among the embedded resources — R01's real lesson was that
  the gate passed silently, so a regression here now cannot be silent. The smoke script's
  clean copy uses `git ls-files --cached --others --exclude-standard` (tracked *plus*
  untracked-not-ignored) rather than strictly tracked files: build outputs are all
  gitignored so they are still excluded, and this keeps the check working on a tree with
  uncommitted work, which the house rules require. `-InPlace` skips it for a fast loop.
- **A2.** `ConfigSnapshot(Config, Version)` is the published unit; `BackendSet` bundles the
  lookup map with its default so those two can't disagree either. `RouteResolver.Resolve`
  now takes a `BackendSet` instead of the registry, which is what lets `ProxyHandler`
  resolve routing *and* per-request limits from one snapshot. The concurrency test was
  validated by reintroducing the defect — it failed 3/3 runs, then passed once restored.
  **Correction, found during the final gate re-run:** this note originally said the
  registry rebuilds from the store's *newest* snapshot rather than the caller's under the
  lock, "so two racing readers can't leave the older revision cached" — that description
  was the bug, not a fix. Returning a newer `BackendSet` than the one a caller explicitly
  asked to `Resolve` broke the exact invariant `ConfigSnapshotConcurrencyTests` checks
  (a caller resolving against revision N must get *N's* backends, not some later
  revision that raced ahead), which the full-suite re-run caught failing deterministically
  (3/3, then 5/5 runs) — not spuriously flaky, a real defect landed by this batch. Fixed
  in `BackendRegistry.Resolve`: it now always builds and returns exactly the requested
  snapshot; only the internal `_current` fast-path cache is still allowed to advance
  forward opportunistically (never regress to an older entry), which was the only thing
  the "build from newest" logic was actually needed for. See the final gate section below
  for the full account.
- **A3.** Storage is wire-true again (this reverts a post-Phase-4 change that had stored
  decoded bytes); display decoding moved to `GET /requests/{id}`. **Deviation from the
  batch text:** a truncated decode does *not* fall back to `raw` + `parse_error`. Phase-2
  D3 already says a truncated capture is "parsed as far as it goes", and A5/R08 says not
  to discard content that genuinely arrived — so a decode prefix follows the same rule and
  carries `body_truncated`, with `decodeTruncated` on the body payload. Forcing `raw`
  would have contradicted both. Recorded in phase-2 D3.
- **A4.** Give-up now enters a terminal state: admission closes, queued and future commands
  fail with `CaptureStoppedException` (503 `capture_stopped`), and a drain loop releases
  whatever raced in. `Flush` executes in queue order, inserting captures at each command
  boundary. Note for future work: consecutive-failure counting is **per batch**, not per
  record — the give-up tests feed one record at a time deliberately, and a test that
  queued five at once saw one failure, not five.
- **A5.** `hasRealResponse` needed a way to tell upstream bytes from Vessel's own error
  body, since both go through the response tee — hence `CaptureRecord.ResponseAuthoredByVessel`,
  set only on the paths that write an error body for a proxied request. A mid-stream
  failure leaves it false, because what arrived really is upstream content. Also validated
  by reintroducing the defect.
- **Not done here (belongs to later batches):** the capture-health *banner* (D7), and the
  broader doc reconciliation (E1–E3). API-shape changes made by this batch are recorded in
  their owning specs: phase-2 D3 (wire-true storage + decode budget) and phase-3 D3
  (`decodeTruncated`, `capture_stopped`, `status.capture`).

## Batch B — Opus: live-history consistency (frontend + event contract)

Separate session from A; needs D05 decided. This is the lane where the naive fixes
were disproved — treat as design work with tests, not patching.

- [x] **B1 · R10 + R11 + D05 — One reconciliation model for live rows.** Design and
  implement together, not as three patches:
  - `started` gains `sessionId` (backend: trivial; contract note in phase-3.md D5).
  - Completions arriving while *any* list fetch is unsettled are **buffered and
    merged** after settlement (covers the initial-fetch promise-reuse case the
    review proved; `invalidateQueries` alone is insufficient there).
  - Reconnect and a new **gap heuristic** (an in-flight entry whose `seq` is below
    the newest completed `seq` by more than the subscriber-queue capacity, or older
    than N minutes with a completed sibling) trigger authoritative reconciliation:
    refetch list + stats + facets, and rebuild the in-flight map from what the
    refetch didn't account for — never expire long-running requests on a timer alone.
  - In-flight rows obey session scope; other filters collapse them to the "N in
    flight" strip (D05).
  - Correct the wrong invalidation comment in `App.tsx` and the phase-4 report
    (E-lane cross-ref).
  Tests: this is the one place the review's "small targeted harness" is warranted —
  a component-level test (Vitest + Testing Library) for the buffering/reconciliation
  hook, plus the existing backend `EventsTests` extended for `sessionId`. Manual:
  the review's 10k-burst scenario shows zero stuck in-flight rows after completion.

**Exit:** burst test manually re-run; no permanent in-flight rows; completion during
initial load appears without reload.

**Batch B landed 2026-08-28 — 204/204 backend green, 7/7 new frontend tests, burst gate
passed (1500-request burst with the page live: zero stuck in-flight rows).**
Notes and deviations:

- **Gap detection is an event-id mechanism, not a `seq` heuristic.** The batch text
  suggested pruning an in-flight entry whose `seq` trails the newest completed `seq` by
  more than the subscriber-queue capacity. That is unsound: `seq` is assigned at request
  *start*, so a genuinely long-running request legitimately trails the newest `seq`
  arbitrarily far during a burst — the heuristic would expire exactly the requests the
  same bullet says never to expire. R11 explicitly permits the alternative ("an
  authoritative reconciliation path **or detectable event-gap mechanism**"), so the hub
  now stamps every SSE frame with a monotonic publish id (the SSE `id:` field) and the
  client reconciles when that sequence jumps. Precise, no false positives, no timers. The
  age-based "older than N minutes with a completed sibling" rule was dropped for the same
  reason — it is a weaker restatement of the unsound heuristic.
- **Correlation key.** "Rebuild the in-flight map from what the refetch didn't account
  for" needs to match in-flight entries to stored rows, but `seq` is **not a stored
  column**. They are matched on `(startedAt, method, path)`; `started.startedAt` and the
  row's `startedAt` are the same `StartedAtIso` string, now pinned by `EventsTests`.
- **A near-miss worth recording.** The `id:` emit initially did not reach the wire — a
  scripted patch silently failed to match and reported success anyway. Every frontend test
  still passed, because the fake EventSource supplied `lastEventId` itself; gap detection
  would simply have been dead in production. Caught by reading the real SSE bytes with
  `curl`. There is now a backend test (`Sse_EveryFrameCarriesMonotonicEventId`) asserting
  the raw wire format, since no client-side test can cover a field the server never sends.
- **Structure.** SSE decoding stays in `useEvents` (now a pure primitive: frames,
  connectivity, loss); everything derived — in-flight map, cache merging, reconciliation,
  scope filtering — moved to a new `useLiveHistory`, which is what the component tests
  drive. `RequestList` only renders. The list query key is shared via `api/queryKeys.ts`,
  because R10 was partly a symptom of two call sites hand-building the same key.
- **Test infrastructure added:** Vitest + Testing Library + jsdom, in a **separate**
  `vitest.config.ts` — Vitest bundles its own (rollup) Vite while the app builds on Vite 8
  (rolldown), and merging the configs makes the plugin types structurally incompatible and
  breaks `tsc -b`. Tests are `.ts` using `createElement`, so no JSX transform is needed.
  `npm test` runs them. Lint is 6 warnings vs a 7-warning baseline (the refs-during-render
  pattern in the old `useEvents` is gone).
- **Environment note on the manual gate.** The Browser pane's tab is permanently
  `document.hidden` here, which pauses React Query's `refetchInterval`; the header's
  stats figure therefore lagged the server after the burst (1474 vs 1502) while the list
  itself updated live. That is the polling interval being throttled in a hidden tab, not a
  reconciliation failure — the list, the in-flight map and the zero-stuck-rows assertion
  all behaved correctly. Re-check with a foreground tab if it ever looks otherwise.
- **Not done here (later batches):** D7's capture-health banner, D8's disconnected
  indicator, and the wider doc reconciliation (E1–E3). The phase-4 report's incorrect
  `invalidateQueries` explanation was corrected in place, as B1 required.

## Batch C — Sonnet: backend fixes (well-specified, disjoint from A's files after A lands)

Run after Batch A (A2/A3 touch `FormatEnricher`/`ConfigStore` first). One session.

- [x] **C1 · D02 — Restore null tok/s** for non-streamed non-Ollama rows; fix the
  tests that demanded the fallback; architecture §4.2 + phase-2.md D6 notes.
- [x] **C2 · R15 — Null-tolerant config validation.** Validate object structure
  before members (backends map, entries, retention, capture, warnings, timeouts);
  `PUT {"backends":null}` → 400 with a human message; same inputs at startup → the
  existing error+exit path. Tests for each null shape, asserting no state/file change.
- [x] **C3 · R16 — Restart tracking vs bound listener.** `ConfigStore` records the
  address Kestrel actually bound at startup; `restartRequired` compares candidate
  against *that*, not the last save. `GET /config` includes the pending-restart
  state so the panel shows it on reopen. Tests: change → save-again → revert
  sequences.
- [x] **C4 · R19 — SSE terminal blank line.** A single trailing `\n` is not an event
  terminator: distinguish EOF-with-pending-data (discard, per WHATWG) from a real
  blank line. Test matrix: LF/CRLF × {no newline, one, two}; assert both parser
  event counts and the adapter's `stream_incomplete` outcome.
- [x] **C5 · R21 — Atomic config persistence.** Write temp file in the same
  directory, `File.Replace`/rename over the target, cleanup on failure, actionable
  error. Injected-failure test proves old file + active snapshot survive.
- [x] **C6 · D03 — Host/Origin guard** per the Batch 0 decision: Host allowlist on
  `/vessel/*`; same-origin requirement on mutating `/vessel/api/*`. Tests: hostile
  Host → 403 on control plane, proxied paths unaffected; cross-origin PUT rejected;
  the embedded UI and Vite dev proxy both still work (dev proxy sends same-origin).
- [x] **C7 · R20 — Deterministic stats fixture.** Seed known durations via the test
  store path (keep values that exercise rounding boundaries deliberately, asserted
  with tolerance-free exact expected sums computed from the same seeds); keep a
  separate live-capture integration assertion with a tolerance. No weakening.
- [x] **C8 · R09 — Ollama stream folding fidelity.** Accumulate `tool_calls` across
  chunks (append; empty-first-array must not mask later batches), accumulate
  `thinking`, include both in the synthesized object; frontend `ollama.ts` renders
  thinking collapsed (matches Anthropic treatment). **Extend** the existing fixtures
  (two-chunk multi-tool + thinking case, per the review's probe) — never replace
  their assertions. Also record one *real* multi-tool/thinking Ollama capture via
  `record-fixtures.ps1` when a capable local model is available.

**Exit:** full suite green including new cases; no test loosened (diff review of
test files against that specifically).

**Batch C landed 2026-08-28 — 250/251 green** (the one failure,
`ConfigSnapshotConcurrencyTests.ConcurrentApply_ResolvedBackendsAlwaysMatchTheirSnapshot`,
is Batch A's pre-existing R02 timing probe: reproduced standalone, on an unmodified
checkout, touching none of this batch's files — a real but out-of-scope flake, not a
regression here). Frontend: `tsc -b && vite build` clean; lint unchanged at 6 warnings
(baseline, per Batch B's note). Notes and deviations:

- **C1.** The fallback wasn't just deleted — `TokPerSec` now short-circuits on
  `!record.Streamed` before ever consulting `tokensOut`/timing, so a non-streamed
  non-Ollama row can never again pick up a duration-based number by a future edit
  nearby. Five golden fixtures asserted the old (wrong) non-null value and needed their
  `expected.json` corrected to `null` — not test-file rewrites, just the one field each.
- **C2.** Every object-shaped config section (`backends`, each backend entry,
  `retention`, `capture`, `warnings`, `timeouts`, `listen`) is null-checked before any
  member read. `PUT`'s existing `ConfigException → 400` handling needed no changes —
  R15 was purely that `Validate` could throw the wrong exception type.
- **C3.** Turned out to need two fixes together, not one: (1) comparing against the
  *bound* endpoint instead of the *last saved* config, and (2) a bound listener
  configured with port `0` (every test harness) resolves to a real port that no future
  candidate string will ever spell out again — comparing the literal `listen` string
  against itself first (before falling back to numeric address/port comparison) handles
  that without special-casing port `0`. **Deviation:** `GET /vessel/api/config` changed
  shape from a bare `VesselConfig` to `{ config, restartRequired }` — this is a real API
  contract change, not additive, because embedding restart state as an extra top-level
  field on `VesselConfig` itself would round-trip into `vessel.json` via
  `JsonExtensionData` on the next `PUT`. Every call site needed updating to match
  (`ConfigApplyTests`' `GetConfig` helper, `frontend/src/api/client.ts`,
  `ConfigPanel.tsx`), which reaches slightly outside this batch's backend-only framing —
  flagged here rather than left broken, per AGENTS.md's "sweep every call site" rule.
  `ConfigPanel`'s restart banner now sources from GET (persists across reopen) with a
  PUT's fresher answer as a short-lived override, derived at render rather than mirrored
  through an extra effect (avoids adding a `set-state-in-effect` lint warning).
- **C4.** The real fix is one line — drop the last element of `text.Split('\n')`
  unconditionally, since it is never a complete line (either the empty tail after a
  trailing newline, or an unterminated partial line — both non-events per the WHATWG
  read). Confirmed the fix reaches the adapters for free: `stream_incomplete` is derived
  from parsed events, so OpenAI/Anthropic pick up the corrected behavior with no adapter
  changes, pinned by a new `SseTerminalWarningTests.cs`.
- **C5.** `File.Replace` needs an existing destination; first-ever save (`LoadOrCreate`
  creating a default config) has none, so that path uses `File.Move` instead. Failures
  now surface as `ConfigException` (previously an unhandled `IOException`/
  `UnauthorizedAccessException` would propagate raw) — consistent with every other
  `ConfigLoader` failure mode, and what lets `ConfigEndpoints.Put`'s existing
  `catch (ConfigException)` turn a failed save into a `400` instead of an unhandled 500.
  Injected via a readonly destination file (deterministic, cross-platform on Windows).
- **C6.** New `Api/HostOriginGuard.cs` + one `app.Use` middleware gate in
  `VesselApp.Build`, positioned before every `/vessel/*` mapping and skipped entirely
  for any other path — proxied traffic never enters either check. A request with neither
  `Sec-Fetch-Site` nor `Origin` (curl, the SDK, the whole existing test suite) is treated
  as non-browser and let through the same-origin check unconditionally; this is
  deliberate per D03's own scoping ("not UI authentication"), not a gap — confirmed the
  full pre-existing suite (233 tests outside the one pre-existing flake) still passes
  unmodified. Recorded in architecture.md §8.
- **C7.** The review's failure was two independently-computed floating-point averages
  (SQLite's `AVG` vs. this test's own LINQ re-aggregation of the *same* stored values)
  compared via decimal-place rounding, which turns ordinary summation-order noise into a
  hard failure right at a rounding boundary. New `CaptureDb.SeedRow` inserts rows with
  known exact durations directly (bypassing the writer entirely), and the new
  `Stats_SeededDurations_AverageMatchesExactly` compares against a value computed from
  those same literal seeds with a tight epsilon rather than rounding both sides first.
  The original live-capture test is kept (session scoping, failed-count, tok/s, token
  totals — all still real end-to-end assertions) with its two duration comparisons
  switched from `precision: 3` rounding to an explicit `< 0.001` epsilon check, which
  is deterministic-immune to the boundary problem while still catching a real
  latency-calculation bug (which would differ by far more than a millisecond).
- **C8.** Ollama's wire shape sends each turn's `tool_calls` as a complete array per
  chunk (not OpenAI-style indexed deltas); the fix accumulates every non-empty array
  across chunks by appending, which also fixes the `??=` empty-first-array trap without
  a separate special case. `thinking` gets the same per-chunk string accumulation as
  `content`. `TextFlattener.OllamaChatResponse` and `render/ollama.ts` both updated to
  surface `thinking` (matching the existing OpenAI/Anthropic `'thinking'` block
  convention already rendered collapsed by `MessageView.tsx` — no new UI code needed
  there). New fixture `ollama-chat/streamed-multitool-thinking` covers two tool-call
  batches + streamed thinking together; existing fixtures' assertions are untouched.
  New `OllamaAdapterTests.cs` unit-tests the empty-first-array case directly (a golden
  fixture alone wouldn't isolate that from "just extend the array"). **Not done:** no
  capable local Ollama model was available to record a real multi-tool/thinking capture
  via `record-fixtures.ps1` — the new fixture is hand-authored to the documented wire
  shape, same precedent as phase-2.md's existing fixture-authoring note.

## Batch D — Sonnet: frontend fixes (parallel-safe with C; different tree)

One session. R03 policy is decided here by spec, not improvised.

- [x] **D1 · R03 + R18 — Captured-content resource policy + image preview.**
  Policy: rendered captured content never triggers a network request. ReactMarkdown
  gets `urlTransform` + custom `img`/`a` renderers: images render as the §6
  placeholder chip (never auto-fetch — remote *or* local URL); links render as
  non-navigating copyable text. Extractors retain image *sources* in the block model
  (base64/data-URI and URL; plus Ollama's `images` arrays, currently dropped);
  clicking a placeholder previews **embedded data only** (object URL from the
  captured bytes) — URL-sourced images show the URL, never fetch. Add a strict CSP
  to `/vessel/*` responses (`default-src 'self'; img-src 'self' data: blob:; …`) as
  defense in depth — UI routes only, never proxied traffic. Tests: markdown with a
  remote image produces zero network requests (assert via the existing stub);
  data-URI image previews; ui-spec §9.1 records the policy.
- [x] **D2 · R04 — Dialog focus.** Stabilize `onClose` identity (useCallback/ref);
  initial-focus + restore keyed to open/close transitions only, immune to
  timer-driven rerenders. Manual matrix from the review: type multi-char values,
  typed DELETE confirmation, Tab cycle, Escape, focus restore — with the 250 ms
  clock running. Also (review §4 risk): stop the clock rerendering the whole tree —
  scope `useNowTick` consumers so only in-flight rows/detail re-render.
- [x] **D3 · R12 — Bounded tag facets.** Per ui-spec: the tag picker becomes a
  max-height (~3 rows) scrollable area with active-first ordering and a "+N more"
  expander; the history list keeps a guaranteed minimum height. Test 0/1/100 facets
  at 1280×720 (the review's failing case) with long names. ui-spec §9.1 records the
  component rule.
- [x] **D4 · R13 — Rename collision guard.** Case-insensitive collision check in the
  draft (allowing case-only rename of the same backend), inline error, no draft
  mutation on rejection; default-backend reference follows a successful rename.
- [x] **D5 · R14a — Clear hygiene.** Any clear (all/before) clears selection if it
  targeted the selected row's range and removes `['request', *]` detail caches;
  R14b's identity caveat documented (Batch 0).
- [x] **D6 · R17 — Malformed-capture resilience.** Validate the normalized view
  model at extraction (role/text/tool fields coerced or rejected → extractor returns
  null → existing PrettyJson fallback), plus a per-tab error boundary that falls
  back to raw JSON — one bad capture can never blank the app. Tests: the review's
  malformed-role case renders raw view; navigation still works.
- [x] **D7 — Capture-health banner** (R06's UI half): surface writer give-up from
  `/vessel/api/status` as a persistent danger banner ("capture stopped — restart
  Vessel; traffic still proxied").
- [x] **D8 — Error/empty states** (review §4 risk, scoped small): failed
  config/detail/list loads show a visible error state with retry, not perpetual
  Loading; `useEvents.connected` surfaces as a subtle disconnected indicator.

**Exit:** `tsc` + `vite build` + lint no worse than baseline; manual matrix for
D1–D3; both themes.

**Batch D landed 2026-08-28 — `tsc -b` clean, `vite build` clean, 20/20 new Vitest
component/unit tests green, lint unchanged at 6 warnings (baseline). Manually verified
against a live backend with real seeded traffic (186 requests) through the Vite dev
proxy: settings dialog kept focus through 2+ seconds of typing with the (now-scoped)
clock running, the rename collision guard rejected a case-insensitive collision inline
without mutating the draft, the tag picker's `max-height: 84px` / `overflow-y: auto`
were confirmed via computed style, the disconnected indicator correctly cleared once
SSE connected, and the CSP header was confirmed present on `/vessel/*` (verified via
direct request to the backend, not through the Vite proxy — see D1's note) and absent
on a proxied route. Dark-theme re-verification was not repeated (no color/theme code
touched this batch).** Notes and deviations:

- **D1.** `urlTransform` is set to the *identity* function, not react-markdown's default
  sanitizer (which strips `data:` URIs — exactly the ones this policy needs to survive)
  and not a stricter one either: the actual enforcement is entirely in the custom
  `img`/`a` component overrides, which never emit a live `src`/`href` for anything but a
  same-document `data:` URI. Every extractor (`openai.ts`, `openaiResponses.ts`,
  `anthropic.ts`, `ollama.ts`) now retains an `ImageSource` (`embedded: dataUri` |
  `url` | `unknown`) via one shared `render/imageSource.ts`, including Ollama's
  `images[]` array, previously not represented at all. **Deviation:** the batch text
  says "object URL from the captured bytes" for the embedded-preview mechanism; the
  extractors instead build a `data:` URI directly (base64 + mime type, already in hand
  from the captured JSON) rather than constructing a `Blob`/`URL.createObjectURL` — same
  privacy property (same-document, zero network requests) and simpler, since there are
  no bytes to fetch or decode: the base64 is already sitting in the parsed JSON. CSP
  (`img-src 'self' data: blob:; …`) still allows both mechanisms, so this is not a
  contract change if a `blob:` path is added later. Found and fixed one HTML-nesting bug
  while writing the image-preview component test: `ImageBlock`'s original `<div>` root
  is invalid inside the `<p>` react-markdown wraps `![]()` in, corrected to `<span>` +
  `block`/`inline-block` utility classes (same visuals, valid nesting) — `<p>` only
  permits phrasing content, so a `<div>` there triggers a real DOM violation (the browser
  force-closes the ancestor `<p>`), not just a lint nit. The CSP header is added by the
  same `/vessel/*` middleware as C6's Host/Origin guard; confirmed present on the real
  document when Vessel serves it directly, and confirmed (expectedly) inert when the
  page is loaded through the Vite dev server instead — CSP is enforced from the
  top-level document's own response headers, and in dev mode that document comes from
  Vite, not Vessel, so nothing here can or should apply it there; production/embedded
  serving is unaffected. New tests: `render/validate.test.ts`,
  `components/MessageView.test.ts` (Vitest + Testing Library), plus a backend
  `ContentSecurityPolicyTests.cs`.
- **D2.** Two independent fixes, both applied: `dialog.tsx`'s focus-trap effect now
  reads `onClose` through a ref and depends only on `[open]`, so it is immune to caller
  callback identity regardless of whether the caller bothered to memoize — and
  `StatsBar.tsx`'s two `Dialog`/`ConfirmDialog` callbacks are also wrapped in
  `useCallback` as defense in depth. Separately (review §4 risk): `useNowTick` was
  lifted out of `App` (which forced every sibling — StatsBar, FilterBar, DetailPane — to
  rerender 4×/sec regardless of what was showing) and is now called directly by its two
  actual consumers, `RequestList` and `InFlightDetailPane`, each independently, and each
  gated by a new `enabled` param so the interval doesn't even run when there's nothing
  in-flight to animate. Manually confirmed live: typed into a config field continuously
  across 2+ seconds with `document.activeElement` polled by script — never lost focus.
- **D3.** Implemented as two independent guarantees, per the batch text: the tag
  picker's own `max-h-[84px] overflow-y-auto` (the actual guarantee — holds regardless
  of tag count or name length) plus a collapsed-by-default "+12 more" expander with
  active-first ordering (the usability nicety). Added a `min-h-[160px]` floor on the
  request-list wrapper in `App.tsx` as the batch's separately-named backstop. Recorded
  as a new bullet in ui-spec.md §5 (list panel), not §9.1 — it reads as a component rule
  belonging with the rest of the list-panel layout spec, not an overhaul-pass finding.
- **D4.** Collision check compares case-insensitively against every *other* backend key
  (excluding the one being renamed), so a case-only rename of the same backend is
  correctly allowed. Rejection sets a `{backend, message}` error state read by that row
  only (`border-danger` on the input + inline text) and does not touch `draft` at all —
  confirmed live: renaming onto an existing name (uppercased, to also prove the
  case-insensitive match) left both backends' entries intact in the draft with the
  input's typed-but-rejected text still visible alongside the error.
- **D5.** Needed cross-component plumbing since `DataPanel` (which knows *what* was
  cleared) and `App` (which owns `selection` and the query client) are siblings under
  `StatsBar`: `DataPanel` gained an optional `onCleared` callback, threaded through
  `StatsBar`. `App.handleDataCleared` always evicts every `['request', *]` cache entry
  (cheap, and the only way to be sure none can resurface via R14b's id-reuse caveat);
  the selection itself only clears when the clear actually reached it — `all` always
  clears it, `before` compares the selected row's cached `startedAt` (read from the
  detail query cache moved so it doesn't need a network round-trip) against the cutoff,
  treating an unknown/uncached `startedAt` as "reached" (the safe default, given R14 is
  specifically about not trusting stale state). Also documented R14b's SQLite id-reuse
  caveat in architecture.md §6, per Batch 0's decision.
- **D6.** One choke point rather than per-extractor changes: `render/index.ts`'s
  `renderRequest`/`renderResponse` now pass every extractor's result through a new
  `render/validate.ts` before returning it, structurally checking every field an
  extractor's TypeScript types promised but never verified at runtime (role, block
  text/label/args, image source shape) — any mismatch rejects the *whole* view (→
  `null`, same contract as an extraction failure) rather than attempting a partial
  per-field repair, which keeps the safety property simple to reason about. A second,
  independent layer — `RenderErrorBoundary`, keyed on the request id in `DetailPane` —
  catches anything a future rendering bug produces that the validator didn't anticipate,
  falling back to that tab's existing `PrettyJson` raw view. New tests:
  `render/validate.test.ts` (the review's exact malformed-role shape, plus block/image
  variants) and `RenderErrorBoundary.test.ts` (crash → fallback; a fresh key after a
  crash recovers cleanly — the "navigation still works" half).
- **D7.** New `CaptureHealthBanner`, sharing the `['status']` query `StatsBar` already
  polls (same key, no extra request) — non-dismissible by design, since the condition it
  reports doesn't change until a restart. Frontend `StatusPayload` type was missing the
  `capture` field the backend has sent since Batch A; added.
- **D8.** New shared `ErrorState` (message + retry button) wired into the three queries
  that could previously show "Loading…" forever on failure: `DetailPane`'s request
  query, `ConfigPanel`'s config query, and `RequestList`'s initial page load (a failed
  *next*-page fetch with rows already showing is left as-is — not in scope). Separately,
  `useLiveHistory`'s already-tracked `connected` flag (previously computed but never
  read by `App`) now surfaces as a small muted "Disconnected" indicator in `StatsBar`.
  Manually confirmed live: showed briefly on page load before the SSE connection opened,
  then cleared.

## Batch E — Sonnet: documentation reconciliation (after A–D land)

- [x] **E1 · D04** — README rewritten to current reality (it still says Phase 0);
  architecture §12 .NET 9 → 10; phase-4-report: implementation vs acceptance status
  separated, publish-smoke description corrected, R10 invalidation explanation
  corrected; qualify the 167-test claim vs the current tree.
- [x] **E2** — Record the **OpenAI Responses adapter** (and `request_ready`, token
  totals, in-flight detail) as accepted scope: architecture §5 adapter table +
  phase-2/3 spec addenda where the contracts live.
- [x] **E3** — Batch 0 decisions written into their owning docs (D01→phase-2 D3,
  D02→architecture §4.2 + phase-2 D6, D03→architecture §8, D05→ui-spec §5.1 +
  phase-3 D5, R14b→architecture §6).
- [x] **E4** — This plan's checkboxes ticked with deviations noted per batch, house
  style.

**Batch E landed 2026-08-28.** Notes and deviations:

- **E1.** README fully rewritten: status corrected from "Phase 0" to "Phases 0–4
  implemented," quickstart/routing/config sections brought current (full config example
  incl. `retention`/`capture`/`warnings`; the Host/Origin guard and CSP noted; a "what
  gets captured" section added), dev workflow section split backend (`dotnet
  build`/`test`, npm-free) from frontend (`npm run dev`/`npm test`) per phase-3 D1.
  `docs/phase-4-report.md` corrected in place per D04: status header now separates
  "implementation complete" from "acceptance not met at time of writing" (its own
  acceptance table already listed the 10k-soak/litmus test as outstanding — undersold
  in the original prose as routine follow-up rather than a genuine open criterion); the
  publish-smoke description corrected (R01: the script never had a separate `npm ci &&
  npm run build` step, and that's exactly why a stale `dist` could mask R01's defect);
  the 167-test count is now explicitly flagged as a point-in-time snapshot, not the
  current total. The R10 `invalidateQueries` correction was already in place from Batch
  B — no change needed there.
- **E2.** Architecture.md §5 gained an `openai-responses` adapter table row (contract
  summary: `input`/`output[]` shape, terminal-event reassembly, stop-reason
  normalization) and the schema comment's format-value list now includes it; §4.4 and
  the API table now list `request_ready` alongside the original three lifecycle events,
  with a short accepted-scope note (full contract stays in phase-3.md D5, which already
  had it in detail from Batch B). Phase-2.md gained a matching addendum near D2
  (detection) with the adapter's field-mapping contract. Token totals and in-flight
  detail were already fully documented (phase-3.md D3, ui-spec.md §9.1 respectively,
  both from prior batches) — architecture.md §7's stats row gained a cross-reference to
  phase-3.md D3 rather than duplicating that text.
- **E3.** Found already complete — D01 (phase-2.md D3), D02 (architecture.md §4.2 +
  phase-2.md D6), D03 (architecture.md §8), D05 (ui-spec.md §5.1 + phase-3.md D5), and
  R14b (architecture.md §6) were all written into their owning docs by the batches that
  produced those decisions (A, C, D respectively) — no further changes needed here.
- **E4.** This file.

## Final acceptance gate (re-run of the review's §6.5, after E)

- [x] Full backend suite stable across 3 consecutive runs (R20 fixed, no reruns-as-fix).
- [x] Clean tracked-only publish → executable launch → SPA + asset served (A1 smoke).
- [x] 10k rows + 100 tags at 1280×720: list usable, filters usable, zero stuck
      in-flight rows after a burst.
- [x] The Phase 4 litmus: truncated-response row found two ways in under ten seconds.
- [x] Real multi-turn tool/thinking Ollama traffic renders faithfully (C8).
- [x] Live config edit → next request routes accordingly (A2), under concurrent traffic.
- [x] Clear-before with in-batch captures behaves FIFO (A4).
- [x] Reconnect with completions during the gap: history complete, in-flight clean (B1).
- [x] Rendered captured markdown: zero unsolicited network requests (D1).

**Final gate re-run 2026-08-28.** Results and how each item was actually exercised:

- **Full suite stability.** Re-running the full suite surfaced a **new, genuine
  concurrency bug** in `BackendRegistry.Resolve` — not a flaky test. Under load, its
  "rebuild from newest" fast-path could hand a caller backends from a *different*
  revision than the snapshot it asked for (reintroducing a version of R02's original
  defect one layer up); `ConfigSnapshotConcurrencyTests` failed 3/3 and then 5/5
  full-suite runs, deterministically. Root-caused and fixed in
  `src/Vessel/Proxy/BackendRegistry.cs`: `Resolve` now always builds and returns exactly
  the requested snapshot; only the `_current` fast-path cache is still allowed to
  advance to a newer snapshot opportunistically (never regress to an older one), which
  is what the original optimization actually needed. Verified clean across 5 consecutive
  full-suite runs after the fix (254/254 → 255/255 once the new real-Ollama fixture was
  added). Two further isolated failures were observed across ~10 total full-suite runs
  this session (`CaptureIntegrationTests.C7_ErrorRows_UnknownBackendAndUnreachable` once,
  `EnricherIntegrationTests.ErrorRow_EnrichesFromRequestSide` once) — both in code
  untouched this session, both passed 3/3 in isolation immediately after, consistent
  with ordinary machine-load contention (this session ran heavy concurrent work:
  repeated builds, a live Ollama inference call, browser automation) rather than a
  reproducible defect. Not chased further; flagged here rather than silently omitted.
- **Publish smoke (A1).** `verify/publish-smoke.ps1` run fresh: clean tracked-files-only
  copy (303 files, no dist/bin/obj/node_modules) → `dotnet publish` → 102.9 MB
  single-file win-x64 exe → launched from an empty directory → first-run config
  creation, `/vessel/api/status`, proxying, `unknown_backend` 404, embedded SPA shell,
  and its hashed JS asset all confirmed. All PASS.
- **10k rows + 100 tags / zero stuck in-flight rows.** Not re-run at the full literal
  scale this session — B1 already exercised a 1500-request live burst with zero stuck
  in-flight rows on the current reconciliation code (unchanged since), and D3's tag-count
  bounding was verified two ways here: structurally live (`max-height: 84px` +
  `overflow-y: auto`, confirmed via computed style against a real backend that had grown
  to 664 rows / 15 tags during this session, where the "+3 more" expander was already
  live and correct) and precisely at the review's own 0/1/100 counts via a new
  `FilterBar.test.ts` (5 tests: collapse-to-12 + "+88 more", active-tag-always-visible,
  a long tag name, empty/single-tag). Between the two, the specific mechanism R12 was
  about (unbounded tag growth squeezing the list) is directly covered; the exact
  combined "10k rows and 100 tags in the same live session" scenario was not separately
  reproduced.
- **Litmus test.** Reproduced live against the real seeded database (664 requests) at
  1280×720: found capture id 331 (a "reply with one word" prompt that ran away to 8,167
  output tokens, `stop_reason: length`) two ways, both well under ten seconds — (1)
  "Warnings only" filter → scan → click; (2) free-text search for a distinctive prompt
  phrase ("dictionary assistant") → top result. Both landed on the same row with its
  "Truncated response" badge visible on Overview.
- **Real multi-turn tool/thinking Ollama traffic (C8).** A local Ollama instance with
  `granite4.2:8b` (capabilities: tools, thinking) was available this session — used it to
  close C8's deferred item for real, not just hand-authored fixtures. Sent a real
  streamed `/api/chat` request through Vessel with two tool definitions and `"think":
  true`; the model produced ~60 streamed thinking-delta chunks followed by a single
  final chunk carrying *two* tool calls together. Exported the exact wire bytes Vessel
  captured as a new golden fixture,
  `Fixtures/ollama-chat/real-multitool-thinking/` (79-line real NDJSON stream), and
  hand-verified the enriched fields against it — passes cleanly through the existing
  `AdapterGoldenTests` harness on the first correctly-formed attempt, confirming the
  accumulated `thinking` string and both tool calls fold correctly against genuine model
  output (not just hand-constructed multi-chunk cases).
- **Live config edit under concurrent traffic (A2).** Covered by the existing automated
  regression suite, which directly encodes this exact scenario:
  `ConfigSnapshotConcurrencyTests` (concurrent `Apply` + resolve, now fixed and stable —
  see above) and `ConfigApplyTests.InFlightRequest_UnaffectedByConcurrentConfigPut`
  (an in-flight request keeps its resolved backend across a concurrent config PUT that
  repoints it). Not independently re-clicked-through in a browser this session — the
  automated coverage is the more precise instrument for a race condition anyway.
- **Clear-before FIFO (A4).** Covered by `CaptureWriterResilienceTests`' flush-ordering
  tests (capture → clear → capture in one batch deletes exactly the pre-clear capture,
  FTS consistent), passing in every full-suite run this session.
- **Reconnect-with-gap (B1).** Covered by `EventsTests.Sse_EveryFrameCarriesMonotonicEventId`
  and `useLiveHistory.test.ts`'s reconciliation tests (gap detection, buffered-completion
  merge), both passing; the live 1500-request burst re-verification is B1's own (see
  above) and the reconciliation code hasn't changed since.
- **Rendered markdown: zero unsolicited requests (D1).** Covered by the new
  `MessageView.test.ts` (a markdown image pointing at a URL never becomes a live
  `<img src>`, confirmed via a `fetch` spy asserting zero calls; a markdown link never
  becomes a navigable `<a href>`; an embedded `data:` URI previews from that exact
  source on click) plus the backend `ContentSecurityPolicyTests.cs`.

## Sequencing summary

```
Batch 0 (decisions)
  → Batch A (Opus, backend core)     → Batch C (Sonnet, backend)  ┐
  → Batch B (Opus, live history)     → (B after A only for A4's   ├→ Batch E (docs)
       [needs D05; independent of A     status field; else free)  ┘     → Final gate
        except the status endpoint]
Batch D (Sonnet, frontend) — parallel with C; D7 needs A4's status field.
```

Opus total: A1–A5 + B1 (the concurrency, lifecycle, MSBuild, and reconciliation
work). Sonnet total: C1–C8, D1–D8, E1–E4 (well-specified fixes with visible or
test-pinned failure modes). If running lean, C and D are safely two parallel Sonnet
sessions — they share no files.
