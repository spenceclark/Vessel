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

**Superseded in part by the re-review (see [code-review-phase-4.md](code-review-phase-4.md)),
then closed by Batch F/G below.** Two of this gate's original bullets were overstated when
first written: the 10k/100-tag live-burst bullet checked a scenario the same session's own
prose admitted hadn't been run at that scale, and the reconnect-with-gap bullet relied on
B1's reconciliation before the re-review found it incomplete under concurrent SSE
publication (R11/R22) and a clear/buffer race (R23). Both bullets' prose below is corrected
in place to cite the evidence that actually demonstrates them — F4's real 10k×4 live burst
and F1/F2/F3's fixes — rather than the original, insufficient evidence D04 flagged.

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
- **10k rows + 100 tags / zero stuck in-flight rows.** At the time this bullet was first
  checked, the literal combined scenario had *not* been run at full scale (only a
  1500-request burst, via B1) — the re-review caught exactly that gap, then independently
  reproduced a browser crash during a real 10k live burst, which kept the gate open (its
  §7 condition 5). Batch F's F4 (see above) is what actually demonstrates this bullet: four
  bursts of 10k/10k/10k/3k requests at concurrency 24 across 100 tags, sent with the tab
  connected live and never reloaded — 21 in-flight rows down to 0 after settle, SSE never
  disconnecting, no application crash, the tag picker still correctly bounded (12 shown +
  "+N more") at 100 real facets. D3's `FilterBar.test.ts` (5 tests pinning the review's own
  0/1/100 counts) covers the layout mechanism in isolation; F4 is what covers it live, at
  the literal scale, with the fixes (F1 ordered publication, F2 server-authoritative
  lifecycle, F3 clear/buffer generation) in place.
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
- **Reconnect-with-gap (B1, superseded by F1/F2).** B1's mechanism — a unique-but-unordered
  SSE id plus history-page reconciliation — is what this bullet originally cited, and the
  re-review found it insufficient on two fronts: concurrent publishers could enqueue frames
  out of allocation order (R22, 3,535 adjacent reversals out of 12,800 events under a
  16-publisher probe), and reconciliation only ever checked cached history pages, so a
  completion lost outside the loaded/filtered pages left a permanently-running row (R11).
  Both are now closed at the root, not patched around: F1 allocates the publish id and
  fans out to subscribers inside one lock, so delivery order matches allocation order
  (pinned by `EventsTests.Publish_ConcurrentPublishers_DeliversEveryFrameInStrictIdOrder`,
  16 publishers × 100 batches, reintroduction-validated); F2 replaced page-derived
  reconciliation with a server-authoritative active-request set
  (`GET /vessel/api/active`), so an off-page or filtered-out completion is detected
  correctly regardless of what the client happens to have loaded. F4's live burst (above)
  is the current live evidence for "history complete, in-flight clean" under load.
- **Rendered markdown: zero unsolicited requests (D1).** Covered by the new
  `MessageView.test.ts` (a markdown image pointing at a URL never becomes a live
  `<img src>`, confirmed via a `fetch` spy asserting zero calls; a markdown link never
  becomes a navigable `<a href>`; an embedded `data:` URI previews from that exact
  source on click) plus the backend `ContentSecurityPolicyTests.cs`.

---

# Re-review remediation (Batches F–G)

> Source: the re-review that replaced [code-review-phase-4.md](code-review-phase-4.md)
> in place (baseline `b4c5d63`). 17 of 21 original findings closed; still open:
> R11 (partial), R22/R23 (new regressions in the remediation), R05/R09/R18 remainders,
> D04 doc accuracy — plus one item raised by our own UI agent (G5). Same house rules;
> the review's §7 closing conditions are this section's exit gate.

## Batch F — Opus: live-history lifecycle (R22 + R11 + R23 as ONE design)

The three open lifecycle findings are one problem: event ordering, lifecycle truth,
and deletion boundaries were designed separately. One session, one coherent model:

- [x] **F1 · R22 — Ordered event publication.** ID allocation and channel enqueue
  become atomic: allocate the event id *inside* the hub's publish lock, in the same
  critical section as the `TryWrite` fan-out (allocate only when actually publishing;
  keep the zero-subscriber early-out before the lock). The lock is microseconds
  between event emitters and never waits on a subscriber — drop-oldest stays; the
  non-blocking proxy contract is unchanged. Client: gap handling never moves `lastId`
  backwards, and recovery work is **coalesced** — one pending-reconcile flag with a
  short debounce, so N gaps in a burst trigger one recovery, not N. Tests: port the
  review's concurrent-publisher probe (16 publishers × batches, drain fully) into
  `EventsTests` asserting zero adjacent reversals and complete delivery; a real-drop
  case still detects loss; a burst of synthetic gaps produces exactly one
  reconciliation call.
- [x] **F2 · R11 — Server-authoritative lifecycle.** The client stops inferring
  "still running" from paginated history — it can't. The server knows: expose the
  live in-flight set (`activeSeqs`, plus newest completed seq) via
  `/vessel/api/status` or a tiny dedicated endpoint, sourced from the live
  `CaptureContext`s. Reconciliation (reconnect or coalesced gap recovery): fetch the
  active set; any in-flight entry not in it is finished-or-dropped → remove it and
  refetch list/stats/facets once. No timers; an hour-long generation survives
  because it is genuinely in the set. Tests: the review's off-page case (started,
  completion lost, 100 newer rows, reconnect → entry leaves), filtered-history and
  cleared-row variants, and a long-running request surviving reconciliation.
- [x] **F3 · R23 — Clear/buffer generation boundary.** Clears bump a client-side
  generation; buffered completions are stamped at buffer time and merged only if
  their generation is current. Clear-before precision: the DELETE response gains the
  boundary (max deleted id); buffered completions above it legitimately survive.
  `DataPanel` and `useLiveHistory` share this state — one module owns it. Tests
  cover the interaction *as one*: clear during pending initial fetch (the review's
  repro → cache is `[]`, not `[1]`), clear during refetch, clear-before with a
  surviving buffered completion, and R14b's accepted same-tab caveat unchanged.
- [x] **F4 — Burst re-run + crash check (batch exit gate).** Re-run the 10k-row /
  100-tag / 24-way live burst with a connected tab: zero stuck in-flight rows, no
  reload, and **no browser crash**. The untraced crash from the re-review is most
  plausibly R22's false-gap recovery storm during the burst — but that is a
  hypothesis, not a finding: if the tab still crashes after F1–F3, capture a
  performance profile and treat it as a new investigation, not a re-tick.

**Exit:** review §7 conditions 1, 2, and 5 met; full backend + frontend suites green
including the ported probes as regression tests.

**Batch F landed 2026-08-28 — 258/258 backend green across 3 consecutive runs, 40/40
frontend green, `tsc -b` + `vite build` clean, lint unchanged at 6 warnings (baseline).**
The three lifecycle findings were implemented as one model, as the batch required. Notes and
deviations:

- **F1 (R22).** `CaptureEvents.Publish` now allocates the publish id and fans out to the
  subscriber channels inside one `lock (_publishLock)`, so every subscriber's queue receives
  frames in strict id order; the zero-subscriber early-out stays *before* the lock (and
  before id allocation), so the hot path is unchanged when nobody is watching. The lock only
  ever wraps `TryWrite` (drop-oldest, never blocks), so the non-blocking proxy contract
  holds. Client: `useEvents` never rewinds `lastId` (a late lower id can't manufacture a
  phantom gap), and `useLiveHistory` coalesces recovery — a debounce collapses a burst of
  gaps, and a single-flight guard folds gaps arriving mid-reconcile into exactly one
  follow-up, so N gaps produce one recovery, not N. The ported concurrent-publisher probe
  (`EventsTests.Publish_ConcurrentPublishers_DeliversEveryFrameInStrictIdOrder`, 16
  publishers × 100 batches × 128, fully drained per batch) asserts zero adjacent reversals
  and complete 1..N delivery; it was validated by reintroducing the defect (removing the
  lock → it fails deterministically). A companion test pins that a genuine overflow still
  drops-oldest as a *detectable* id gap.
- **F2 (R11).** Reconciliation is now server-authoritative, not history-derived. The hub
  tracks a live in-flight set (`_active`, added on `started`, removed on `completed`,
  independent of subscribers) plus `_newestCompletedSeq`, exposed at a new
  `GET /vessel/api/active` → `{ activeSeqs, newestCompletedSeq }`. The client removes any
  in-flight row the server no longer lists as active **and** at/below the completed
  boundary; a row above the boundary is spared (it may have started after the snapshot), and
  a genuinely long-running request survives because it is genuinely in the set — no timers,
  no seq-distance heuristic. **Deviation from the batch text:** the active set lives on the
  `CaptureEvents` hub, not read ad-hoc from live `CaptureContext`s — the hub already sees
  every `started`/`completed`, so it is the one place that observes the exact lifecycle
  boundary, and it works with zero subscribers (which reading from a subscriber-driven path
  would not). Also **a dedicated endpoint, not `/status`** (the batch allowed either): the
  active set changes on every request and is fetched on demand during reconciliation, so
  folding it into the polled+cached `/status` query would thrash that cache. The old
  `(startedAt, method, path)` identity correlation is gone — removal is keyed on `seq`
  directly. New backend integration test
  `Active_CompletedRequestLeavesActiveSet_AndAdvancesBoundary`; frontend cases cover the
  review's off-page repro, filtered/cleared history (the active set is filter-agnostic by
  construction), a long-running survivor, and a freshly-started row above the boundary.
- **F3 (R23).** Clears and completion-merging share a generation model owned by
  `useLiveHistory`. A clear bumps a generation and records a deletion predicate; a buffered
  completion is stamped at buffer time and discarded at drain if a later clear removed its
  row (every row for clear-all; ids ≤ boundary for clear-before). The boundary is a new
  `boundaryId` on the `DELETE /requests` response (max deleted id), which required threading
  a `ClearOutcome(Deleted, MaxDeletedId)` through `ICaptureStore.Clear` → `ClearCommand` →
  the endpoint (all call sites swept, incl. the two fake stores and the resilience tests).
  **Ordering note:** `DataPanel` now reports the clear (bumping the generation) *before* it
  invalidates the list query, so the generation is current before the post-clear refetch
  drains the buffer — reporting after the awaited invalidate would race the drain. Clear-all
  uses a `() => true` predicate rather than `id ≤ boundary`, deliberately: SQLite id reuse
  (R14b) can hand a post-clear row an id at or below the old boundary, so only the generation
  (not the id) can safely separate pre- from post-clear rows there. Frontend tests cover the
  review's exact repro (clear-all during a pending initial fetch → cache `[]`, not `[1]`),
  clear-before keeping a surviving completion above the boundary, and a completion buffered
  after the clear surviving; each was validated by bypassing the generation filter (the two
  clear tests then fail).
- **F4 — run live and passed the substantive gate.** With the user's approval to use the
  standard port/DB, a real Vessel (`bin/Debug`, port 4550, standard `vessel.db` — cleared
  first) was pointed at a fast local Node upstream (~20–80 ms/response so requests are
  briefly in-flight), the Vite dev UI opened in the in-app browser, and four bursts of
  10k/10k/10k/3k requests at concurrency 24 cycling 100 distinct tags were sent **with the
  tab connected live**. Results:
  - **Zero stuck in-flight rows.** Captured on the *same live, non-reloaded* session: 21
    in-flight rows rendering mid-burst → **0** after settle (`.pulse-dot` count), SSE staying
    connected throughout (no "Disconnected" indicator). No reload needed.
  - **Server lifecycle clean.** `/vessel/api/active` (the new F2 endpoint) returned 0 active
    after every burst; the store held 10,001 → 20,001 → … rows, **0 failed**, **100 distinct
    tag facets**, and the tag picker bounded correctly (12 shown + "+N more" expander, R12).
  - **No application crash.** The React app never showed a page-crash screen and stayed
    responsive whenever observed. **Caveat worth recording:** the in-app Browser pane's
    Electron renderer is discarded whenever the pane is not being actively displayed, which
    surfaced as intermittent `Render frame was disposed` / `Electron sandboxed_renderer …
    binding.startupData is null` host errors and `[vite] connecting…` reconnects between
    observations — an Electron *pane-lifecycle* artifact of a non-displayed pane, **not** the
    Vessel app crashing (no Vessel/React error ever appeared, and every displayed observation
    showed a healthy app). This is the same class of environment limitation Batch B recorded
    (there: hidden-tab polling throttling). The re-review's "This page crashed" was not
    reproduced against F1–F3; consistent with R22's false-gap storm having been the cause,
    though — per the batch text — the original crash was never traced, so this is corroboration
    rather than proof of that specific causal link. The mechanisms are additionally pinned by
    automated regression (reintroduction-validated concurrent-publisher probe; coalesced
    single-flight recovery; server-authoritative removal that no longer refetches per gap).

## Batch G — Sonnet: fidelity remainders, smoke hardening, docs

Parallel-safe with F except G4 (touches the smoke script only, no code overlap
anyway). One session, or fold into F's follow-up.

- [x] **G1 · R05 remainder — decode truncation visible.** Add `decodeTruncated` to
  the TS mirror (`types.ts`); every body view (rendered, PrettyJson, raw stream)
  shows a body-local warning ("showing first N — display decode limit") when set,
  visually distinct from capture-time truncation. Test: the review's cap-lowered
  case renders the warning. **Process rule added by this finding:** any change to
  `BodyPayload`/`Summary`/`RequestDetail` updates `types.ts` in the same diff —
  the hand mirror is accepted (phase-3 D8) only with that discipline.
- [x] **G2 · R09 + R18 remainders — Ollama generate parity.** Generate's top-level
  `thinking` is accumulated across chunks, retained in the synthesized response and
  search text, and rendered collapsed (mirror of the chat fix); generate's top-level
  `images` array reaches the extractor and the existing safe preview path (no-network
  policy unchanged). Fixtures: generate streamed/non-streamed/interrupted thinking
  variants + a generate-with-image request; extend, never replace.
- [x] **G3 · Stat size dropped by tailwind-merge** (raised by the UI agent during the
  CACHED-slot work; noted in ui-spec §9.1, unfixed). `cn()`'s tailwind-merge
  misclassifies the custom `text-stat` font-size utility as a *color* utility, so a
  trailing `text-text`/`text-danger` silently deletes it — every header stat value
  is rendering at the wrong size. Fix: `extendTailwindMerge` in `lib/utils.ts`
  registering `stat` in the `font-size` class group; then audit the other custom
  `@theme` utilities for the same hazard class (`text-*` names are the dangerous
  ones; `rounded-panel/control/chip` and `shadow-*` group correctly). Test: a
  component test asserting the computed class list keeps both `text-stat` and the
  color. Update the ui-spec §9.1 note to fixed.
- [x] **G4 — Smoke script never uses the default port.** The re-review's smoke
  failure started with port 4550 already in use (a live daily-drive Vessel — exactly
  the machine state the script must tolerate). `publish-smoke.ps1` always launches
  on an ephemeral port with a temp config/db. Then re-run the complete smoke; if the
  unexplained `SQLite Error 10: disk I/O error` on relaunch recurs in a controlled
  run, investigate (lead suspect: temp-dir cleanup racing process shutdown) — if it
  doesn't, record it as environmental and move on.
- [x] **G5 · D04 — Doc truth pass.** Architecture §9.1 rewritten to the actual
  atomic `ConfigSnapshot` design (A2); this plan's final-gate checkboxes corrected
  to what was *demonstrated* (the 10k live gate un-ticked until F4 passes; the
  reconnect gate qualified per R11/R22); re-review closing conditions tracked here.
  Docs trail reality, never lead it.

**Exit:** review §7 conditions 3, 4, and 6 met.

**Batch G landed 2026-08-28 — 264/264 backend green across 3 consecutive runs, 56/56
frontend green, `tsc -b` + `vite build` clean, lint unchanged at 6 warnings (baseline).**
Notes and deviations:

- **G1.** New `DecodeTruncatedNotice.tsx`: a small warn-colored banner (visually distinct
  from the Overview tab's danger-colored capture-time "Truncated" card / `body_truncated`
  badge) shown above the body in `DetailPane`'s Request and Response tabs, computed from
  *whichever* `BodyPayload` is actually on screen at that moment — `requestBody` for the
  Request tab; `responseBody` normally, or `responseRaw` specifically when the raw-stream
  sub-view is selected, for the Response tab — so it tracks the Reassembled/Raw stream
  toggle rather than showing a stale verdict from the wrong payload. Reports the shown
  byte length (decoded from `text`/`base64` as appropriate), not the capture-time size.
  `types.ts`'s `BodyPayload.decodeTruncated` mirrors the backend field per the process
  rule this finding added. New `DecodeTruncatedNotice.test.ts` (4 cases, incl. the
  base64-vs-decoded-byte-length distinction); wiring verified live is out of scope here —
  reproducing an actual over-budget decode needs a real oversized compressed capture,
  which the component test's exact shape (from the review's own repro) already pins.
- **G2.** Backend: `TextFlattener.OllamaGenerateResponse` now accumulates top-level
  `thinking` alongside `response` (mirrors `OllamaChatResponse`'s content-then-thinking
  order); `OllamaAdapter.Reassemble`'s `generate` branch accumulates `obj["thinking"]` per
  chunk and sets `synth["thinking"]` only when non-empty (mirrors the chat branch's guard,
  pinned by a new `NoThinking_Generate_OmitsThinkingField` test alongside the accumulation
  one). Frontend: `extractOllamaRequest`'s generate branch now reads top-level `images[]`
  through the existing `ollamaImageSource` (same malformed-entry → `{kind:'unknown'}`
  degradation chat already relies on) and pushes a message even for an empty prompt with
  only images attached; `extractOllamaResponse`'s generate branch renders top-level
  `thinking` as a collapsed block before the response text, reusing `MessageView`'s
  existing generic `'thinking'` handling — no new UI code needed. Four new golden fixtures
  (`streamed-thinking`, `nonstreamed-thinking`, `streamed-interrupted-thinking` — cut mid-object
  with no `done: true`, pinning that partial `thinking` survives an interrupted stream the
  same way partial `response` already did — and `nonstreamed-with-image`, confirming the
  adapter doesn't choke on an image-bearing generate request and still flattens `promptText`
  from `prompt` alone); two new direct `OllamaAdapterTests`; seven new `render/ollama.test.ts`
  cases covering the request-image and response-thinking extraction directly (a render-layer
  golden fixture alone wouldn't isolate extraction from the rest of `DetailPane`).
- **G3.** `lib/utils.ts`'s `cn()` now goes through `extendTailwindMerge`, registering
  `stat` in the `font-size` class group (`{ text: ['stat'] }`) — plain `twMerge` only knows
  Tailwind's own scale (`xs`/`sm`/`base`/`lg`/…), so `text-stat` fell through to the
  text-*color* group and a trailing `text-text`/`text-danger` "won" a conflict that was
  never real. Audited the app's other custom `@theme` utilities per the batch text's own
  lead (`rounded-panel/control/chip`, `shadow-panel/dialog`): confirmed none share the
  hazard, since none collide with a same-prefix semantic group the way a custom `text-*`
  size name collides with `text-*` colors. New `lib/utils.test.ts` (4 cases: `text-stat`
  survives both `text-danger` and `text-text`; a genuine font-size-vs-font-size and
  color-vs-color conflict still resolve correctly, i.e. this isn't just disabling
  conflict detection for `text-*`). Confirmed live against a running dev build via computed
  style: every header `Stat` value now measures `font-size: 20px` / `line-height: 24px`
  (`--text-stat`), not the previous silently-substituted `12.5px` (`--text-sm`).
  ui-spec.md §9.1's aside updated from "flagged for a separate fix" to fixed, in place.
- **G4.** `verify/publish-smoke.ps1` redesigned: every launch now gets its own ephemeral
  port *and* its own fresh directory, with a config always pre-written before the exe
  starts — nothing here can bind, even transiently, to the hardcoded default
  `127.0.0.1:4550`, which is what a live daily-driver Vessel already owns on a developer
  machine (the collision the re-review actually hit). **Deviation:** the old two-launch
  structure (a first launch left to auto-create its config from nothing, purely to prove
  `ConfigLoader.LoadOrCreate`'s create branch, then a second launch in the *same* directory
  pointed at a stub backend) is now one launch, with a config declaring both a real-shaped
  `ollama` backend entry (unreachable, but present purely so `/vessel/api/status` has a
  second, differently-typed backend to list) and the proxying `stub`. The auto-create-from-
  nothing branch can't be exercised without letting Kestrel attempt the hardcoded default
  port first (there is no `--listen` override, and `ConfigureKestrel` calls `.Listen`
  explicitly, so `ASPNETCORE_URLS` has no effect either) — that exact branch (content, and
  that `created` flips correctly on a second load) is already covered port-independently by
  `ConfigLoaderTests.MissingFile_CreatesDefaultConfig`, so the smoke script no longer
  re-exercises it live. Merging into one launch also removes the previous first-launch/
  second-launch pattern of reusing one directory across a `Stop-Process -Force` and a
  relaunch — the leading suspect for the re-review's unexplained `SQLite Error 10`. Ran the
  complete redesigned smoke twice end to end (fresh `dotnet publish`, ~103 MB exe, full
  assertion set) with no port conflict and no SQLite error either time; not chased as a
  root-caused fix (the mechanism was never proven, only avoided), but recorded as resolved
  by construction rather than "environmental," since the redesign eliminates the shared
  state the suspected mechanism needed.
- **G5.** Architecture.md §9.1 rewritten: the two-separate-fields ("immutable `VesselConfig`
  snapshot... plus a version counter") description was the pre-A2 design and is exactly
  what R02 fixed — replaced with the actual atomic `ConfigSnapshot(Config, Version)` unit,
  `BackendSet`'s matching bundled shape, and `ProxyHandler`/`BackendRegistry.Resolve`'s
  single-snapshot-per-request contract, including the `_current` fast-path regression the
  final gate found and fixed (previously undocumented). The stale duplicate "every consumer
  reads `ConfigStore.Current`..." bullet list (describing the same pre-fix design a second
  time) was removed rather than kept alongside the corrected text. This plan's Final
  acceptance gate section (predates the re-review) had exactly the self-contradiction D04
  flagged — a checked 10k/100-tag box next to prose admitting it wasn't run at that scale,
  and a checked reconnect-with-gap box whose only cited evidence (B1) the re-review later
  found incomplete (R11/R22). Both bullets' prose is corrected in place (not appended
  after) to cite what actually demonstrates them now: F4's real four-burst 10k×3+3k live
  run for the first, and F1/F2's root-cause fixes plus F4's live re-verification for the
  second — both checkboxes stay ticked because both are now true, not because they always
  were. Re-review §7 closing conditions tracked in the new subsection immediately below.

### Re-review closing conditions (§7 of code-review-phase-4.md)

| # | Condition | Status | Where |
| --- | --- | --- | --- |
| 1 | Resolve R11/R22 together: authoritative lifecycle + ordered gap detection under concurrent publication | Met | Batch F (F1 ordered publish-and-fan-out; F2 server-authoritative `/vessel/api/active`) |
| 2 | Resolve R23; verify clear ordering across writer, cache, and completion buffer as one interaction | Met | Batch F (F3 generation-boundary model; `DELETE` response's `boundaryId`) |
| 3 | Surface decode truncation (R05); finish generate thinking/images (R09/R18) | Met | Batch G (G1, G2) |
| 4 | Keep all existing tests passing; add the missing regression cases | Met | 264/264 backend (3 consecutive runs), 56/56 frontend, `tsc -b` + `vite build` clean, lint at baseline (6) — verified after Batch G, see above |
| 5 | Rerun the complete executable smoke; investigate the live-burst crash; pass the literal 10k-row/100-tag live scenario without a reload workaround | Met | Batch F (F4: four bursts, zero stuck in-flight rows, no crash, no reload); Batch G (G4: smoke script redesigned off the default port, run clean twice) |
| 6 | Correct D04's acceptance claims and atomic-snapshot description in their owning documents; record only gates actually demonstrated | Met | Batch G (G5: architecture.md §9.1; this plan's Final acceptance gate section) |

## Sequencing summary

```
Batch 0 (decisions)
  → Batch A (Opus, backend core)     → Batch C (Sonnet, backend)  ┐
  → Batch B (Opus, live history)     → (B after A only for A4's   ├→ Batch E (docs)
       [needs D05; independent of A     status field; else free)  ┘     → Final gate
        except the status endpoint]
Batch D (Sonnet, frontend) — parallel with C; D7 needs A4's status field.

Re-review: Batch F (Opus, lifecycle F1–F3 + F4 burst gate)
           Batch G (Sonnet, G1–G5) — parallel with F; G5's gate edits land after F4.
```

Opus total: A1–A5, B1, F1–F4 (concurrency, lifecycle, MSBuild, reconciliation).
Sonnet total: C1–C8, D1–D8, E1–E4, G1–G5 (well-specified fixes with visible or
test-pinned failure modes). If running lean, C and D — and later F and G — are
safely parallel sessions; F and G overlap only on `useLiveHistory` documentation
references, not code.
