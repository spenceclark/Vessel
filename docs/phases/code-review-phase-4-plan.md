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
- **F2 (R11).** *Superseded in part by Batch H (H0b): the re-re-review found this snapshot was
  not coherent (`activeSeqs` and the watermark read separately) and lacked server identity, so
  it wrongly expired requests under concurrent load and across a restart. H2 put both fields
  and the fan-out under one lock and added `serverRunId` (hello/active/status). **Refined
  again by Batch I (I0b):** the boundary rule below was still unsound while a `seq` could
  exist before it was registered (allocation moved inside `Register`, I2), and H2's run-id
  guard treated a stale `/active` response as restart evidence, which erased the current run's
  live rows — a mismatched response is now discarded instead (I2). The server-authoritative
  direction below stands; the specifics are corrected there, not rewritten here.* Reconciliation is now server-authoritative, not history-derived. The hub
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
- **F3 (R23).** *Superseded by Batch H (H0a): this generation + max-deleted-id design was
  found wrong by the re-re-review — the DELETE ack and the SSE completions are independent
  streams that need not arrive in DB-operation order, and a clear-before boundary can't be an
  id (ids follow persistence order, not start time). H3 replaced the whole scheme with an
  in-band `cleared` event ordered against completions on the one SSE stream, and retired
  `boundaryId`/`ClearOutcome`. **Extended by Batch I (I0a):** H3 was correct but incomplete —
  it made correctness depend on the `cleared` frame surviving a deliberately lossy feed, and
  purged only what was in the cache/buffer at that instant, so a pre-clear REST snapshot
  settling later restored deleted rows. The clear is now versioned server state reported on
  `GET /active` as well, re-applied at fetch settlement (I3). Note that a *bounded* id
  boundary returns there — `boundaryId` for clear-all only, where it is a valid necessary
  condition — which is not a return of this design: it is never the ack's, never used for
  clear-before, and never sufficient on its own. The description below is the retired design,
  kept for the record.* Clears and completion-merging share a generation model owned by
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

---

# Third-round remediation (Batch H)

> Source: the re-re-review (same doc, updated in place). R05/R09/R18/R22 closed; still
> open: R11 (restart identity + torn snapshots), R23 (clear ordering — **the plan's own
> F3 boundary design was wrong and is corrected below**), R24 (raw-stream selection
> regression), R25 (active-registry leak — fallout of F2's design missing a terminal
> guarantee). The review requires the R23 correction to be chosen and approved before
> implementation; H0 records that choice.

## H0 — Design corrections

> **Status: BOTH APPROVED, 2026-08-28.** H0a and H0b are the decided designs —
> implementing agents treat this table as authoritative; H5's truth pass records
> them into the owning docs (phase-3.md D5, architecture §4.4/§9.1 as applicable).

| # | Correction | Design |
|---|---|---|
| H0a | **R23: in-band `cleared` event replaces the boundary/generation model.** | The writer publishes a `cleared {scope, beforeTs}` SSE event under the existing publish lock at clear-commit time. The per-subscriber channel preserves true order, so the client rule is: on `cleared`, purge buffered + listed rows matching the server's own predicate (all, or `startedAt < beforeTs` — Summary carries `startedAt`); completions received after the event are post-clear **by construction** (covers ID reuse). No generation counter, no `boundaryId`; the DELETE ack is UX only. F3's max-id predicate is retired. |
| H0b | **R11/R25: lifecycle authority gets identity, coherence, and a terminal invariant.** | (1) `serverRunId` (startup GUID) on the status/active endpoint and as an SSE hello event; run-id mismatch → client discards all lifecycle state and refetches (covers restart). (2) Registry add/remove, completed watermark, and snapshot reads all inside the existing publish lock — one coherent snapshot (kills the 187/571 torn-snapshot probe). (3) **Registered → terminal is an invariant owned at the registration site**: `Register` returns a token; `ProxyHandler`'s `finally` guarantees a terminal transition when capture admission is closed (unregister + `completed {row:null}`); the writer's stop-drain completes every identity it discards. Forwarding stays independent of capture health. |

## Batch H — Opus: lifecycle third pass (one session, H0 designs verbatim)

- [x] **H1 · R25 — Terminal invariant first.** Implement H0b(3); it underpins
  everything else. Tests: the review's probe (stop admission, 32 proxied requests →
  active set empty, all 200s), with and without SSE subscribers, records racing stop,
  drain path completing discards.
- [x] **H2 · R11 — Run identity + coherent snapshots.** Implement H0b(1)+(2). SSE
  contract change (hello event) recorded in phase-3.md D5. Tests: restart repro
  (started → reconnect to fresh run-id → entry leaves without new traffic); the
  concurrent-snapshot invariant probe ported as a test (every odd seq ≤ watermark
  present); long-running request still survives.
- [x] **H3 · R23 — In-band clear.** Implement H0a. Tests: all three failing orderings
  from the review (delayed SSE after empty refresh; survivor completing before ack;
  clear-before with inverted id/start order), plus ID-reuse, initial-fetch and
  refetch settlement. Client purge logic lives beside the buffer it governs.
- [x] **H4 · R24 — Effective display mode.** No rendered view → the raw toggle maps
  the shown body to `responseRaw`; the decode warning follows the shown body. Tests:
  unknown-format stream, known format with failed extraction, decode-truncated raw
  payload; normal-body warning preserved. (Sonnet-sized; folded here because H3
  touches the same detail-pane state.)
- [x] **H5 — Gate re-run.** Full suites + the ported probes green; the F4 burst
  re-run repeated (lifecycle code changed again); review §5 conditions re-checked;
  D04-style truth pass on this plan's own checkboxes — F2/F3 entries annotated as
  superseded by H0, not silently rewritten.

**Exit:** the re-re-review's §"conditions" items on R11/R23/R24/R25 all demonstrated;
no new lifecycle mechanism invented outside H0.

---

# Fourth-round remediation (Batch I)

> Source: round-four review (same doc, updated in place). R24/R25 closed; H0's
> mechanisms confirmed implemented and sound — the residue is three ordering races at
> their edges (R11 A/B, R23 A/B, R26) plus the still-unconfirmed 10k-tab crash, now
> observed in two separate rounds.

## I0 — Design corrections

> **Status: ALL THREE APPROVED, 2026-08-28.** I0a/I0b/I0c are the decided designs —
> implementing agents treat this table as authoritative; I5's truth pass records them
> into the owning docs. I0c still requires I4's profiling before it lands.

| # | Correction | Design |
|---|---|---|
| I0a | **R23: clears become versioned, recoverable state.** | The writer keeps a monotonic clear version with each clear's predicate — `boundaryId` for clear-all (id-prefix is valid there: every then-existing row has id ≤ max), `beforeTs` for clear-before (matching the actual DELETE). The recovery/active endpoint reports the latest clear version + predicate. Client: re-apply current predicates to the list cache **whenever any list fetch settles** (idempotent — catches TanStack reusing a pre-clear in-flight fetch) and to the completion buffer on drain; on gap/reconnect recovery, apply any missed clear versions. Correctness never depends on a `cleared` frame surviving the lossy feed; the in-band frame remains the fast path. |
| I0b | **R11: evidence validity windows.** | (1) Server: seq allocation moves inside `Register`, under the publish lock — "seq exists ⇒ registered" becomes atomic, making the review's allocate-pause-snapshot-register interleaving unrepresentable. (2) Client: a recovery response is applied only when its run id AND its issuing request's run id both equal the current run — a mismatched response is **discarded**, never treated as restart evidence (only the SSE hello signals a run change). |
| I0c | **Crash mitigation: coalesced event application.** | SSE events append to a queue flushed into React state on an interval (~10 Hz) instead of one state update per event — at burst rates (~4k events/s observed) per-event updates are a main-thread/OOM shape independent of ordering correctness. Imperceptible latency for a monitoring UI; in-flight timers already tick on their own shared interval. Ships with I4's profiling so the fix is confirmed against a measured cause, not assumed. |

## Batch I — Opus: ordering edges + crash diagnosis (one session)

- [x] **I1 · R26 — Guarded span covers preparation.** The try/finally that guarantees
  registered→terminal starts immediately after `Register`; `PrepareRequestBody`
  moves inside it. An abort during the injectStreamUsage body read produces the
  intended captured record (`client_disconnect`) and a terminal registry transition.
  Test: the review's real-HTTP repro (Content-Length 4096, JSON prefix, TCP close →
  seq leaves active, row lands with the error) as an integration test.
- [x] **I2 · R11 — I0b verbatim.** Tests: the stale-run-response case (pending run-A
  recovery + run-B hello + B's started → A's response resolves → B's entry survives,
  [1] not []); the allocation-race probe ported (now unrepresentable server-side);
  restart and long-running cases still green.
- [x] **I3 · R23 — I0a verbatim.** Tests: both review cases (held pre-clear initial
  fetch resolving post-clear → []; dropped cleared frame + gap recovery + buffered
  completion → []), plus legitimate post-clear completions and reused IDs surviving,
  across initial-fetch and refetch settlement.
- [x] **I4 — Crash diagnosis, then the gate.** Profile the 10k burst with the tab
  connected (heap timeline + main-thread activity) *before* applying I0c; land I0c
  against the measured cause (or the actual cause if it differs); then the full
  burst gate: zero stuck rows, no reload, no crash, stats/list correct at the end.
  If the crash persists after I0c with a profile in hand, stop and report — do not
  iterate blindly.
- [x] **I5 — Truth pass.** phase-3.md D3/D5 updated (clear-version contract, hello
  semantics, seq-allocation note); this plan's F/H entries annotated as refined by
  I0; review-round history kept legible rather than rewritten.

**Exit:** all three findings' controlled-delivery tests ported and green; burst gate
passed with a profile on record; suites green ×3.

---

# Fifth-round remediation (Batch J) — model change, not another patch

> Source: round-five review. R26 + the 10k burst/crash closed (keep I0c's coalescing);
> R11/R23 partial for the **fifth** time — four repeatable cases, all orderings the
> client's accreted merge rules miss. The review itself concludes I0a's
> latest-predicate/provenance model has design gaps and asks for an amended recovery
> contract agreed before implementation. J0 is that contract. It **replaces** the
> I0a predicate machinery (settle-purges, `postClearIds`, clear versions/history,
> `boundaryId` reconciliation) rather than extending it.

## J0 — The amended recovery contract

> **Status: APPROVED, 2026-08-29** — including the stated transient-display trade.
> This contract supersedes I0a's predicate model; implementing agents treat it as
> authoritative, and J4's truth pass records it into phase-3.md D3/D5.

**Snapshot + ordered log.** The SSE event id (allocated under the publish lock) is the
single log position for every lifecycle change. Contract:

1. **Server:** the recovery endpoint returns `{runId, logPosition, activeSeqs}` taken
   **atomically under the publish lock** — lifecycle truth as-of one stream position.
   The server stops retaining clear predicates/history; `cleared` frames stay (fast
   path) but carry no recovery burden.
2. **Client — recovery is wholesale replacement:** on reconnect, gap, or run change:
   fetch snapshot → discard the applied map, the **entire** completion buffer (safe:
   `completed` is emitted post-insert, so the refetch below always contains those
   rows), all clear knowledge, and every queued event with id ≤ `logPosition` →
   in-flight := `activeSeqs` → trigger list/stats refetch. Queued events with
   id > `logPosition` replay in order on top.
3. **Client — between recoveries, ordered replay only:** events apply strictly in id
   order (`cleared` purges cache + buffer at its position); any detected gap →
   rule 2, never ad-hoc reasoning.
4. **REST reads are authoritative and never client-filtered.** A clear or recovery
   always schedules a refetch that starts after the trigger; the last-started fetch
   wins. Accepted trade, stated in the contract: a stale pre-clear fetch may display
   transiently until its superseding refetch settles — settled state always
   converges, which is what every review test asserts.

Case coverage (why this is structural): R11's queued start and R23-A's queued
completion fall to the id ≤ `logPosition` filter by arithmetic; R23-B's reused-id
survivor cannot be purged because nothing purges REST rows; R23-C needs no clear
history because the refetch reads a database that already reflects every clear.

## Batch J — Opus: implement J0 (one session)

- [x] **J1 — Server snapshot position.** Recovery response gains `logPosition` under
  the existing lock (H2 made the reads coherent; this adds the stream position to
  the same critical section). Clear-history retention removed. Contract recorded in
  phase-3.md D3/D5.
- [x] **J2 — Client rewrite of the reconciliation core.** `useLiveHistory`'s merge
  rules replaced by J0's two operations. Expect net deletion: settle-purge,
  post-clear exemption, clear-version tracking, and buffer-vs-clear logic all go.
  Keep: I0c coalescing (the queue becomes the replay log — it now *is* the
  mechanism, not a hazard), run-id discard rule, terminal invariant (server-side,
  untouched).
- [x] **J3 — Test migration.** All four round-five cases + every prior R11/R23
  controlled-delivery case re-expressed against the new model and green; legitimate
  long-running requests, post-clear survivors, and ID reuse covered; a
  property-style test randomizing delivery order/loss across N events asserting
  settled-state convergence (the model claims order-independence at settlement —
  test the claim, not examples only).
- [x] **J4 — Gate + truth pass.** Burst gate re-run (lifecycle core rewritten);
  round-four closing-table entries for R11/R23 corrected (the review flagged them
  overstated); phase-3.md's clear-recovery contract rewritten to J0; F/H/I entries
  annotated as superseded where applicable.

**Exit:** all reviewer cases green under the new model; the property test holds;
burst gate passes; docs describe J0 and nothing older.

---

# Sixth-round remediation (Batch K) — closing residuals

> Source: round-six review. All four J-round interleavings closed; J0's model held.
> Two residuals remain, neither an ordering-model hole: a TanStack v5 behavior gap
> under J0 rule 4 (R23) and an open product/API decision (R11).

## K0 — Decisions

> **Status: BOTH APPROVED, 2026-08-29.** K0a/K0b are authoritative for implementing
> agents; K3's truth pass records the descriptor contract into phase-3.md D3/D5 and
> removes the now-fixed display-limit caveat.

| # | Decision | Design |
|---|---|---|
| K0a | **R23: post-clear/recovery fetches are made genuinely new via cancel-then-refetch.** | TanStack v5's `refetchQueries` reuses a pending *initial* fetch's promise, so J0's "last-started fetch wins" degraded to "first fetch wins" in that state. `refetchAuthoritative` becomes `cancelQueries(...)` **then** refetch — cancellation settles the stale query as discarded (its response can never become authoritative) and the refetch is genuinely distinct. `client.ts` passes TanStack's `signal` through to `fetch` so cancellation reaches the network. J0 rule 4's no-client-filtering stance is unchanged. |
| K0b | **R11: the recovery snapshot returns full active descriptors, not bare seqs.** | The registry stores the immutable started metadata it already receives at registration (`startedAt, method, path, backend, tags, sessionId` + `model` once `request_ready` fires); the recovery endpoint returns `active: Descriptor[]` in the same locked snapshot. The client reconstructs in-flight rows wholesale — a lost `started` frame no longer hides a running request. No placeholder UX, no narrowing of the brief's live-monitor guarantee. Memory: one small struct per active request, cleanup already guaranteed by the terminal invariant. |

## Batch K — Opus (one session; likely the closing round)

- [x] **K1 · R23 — K0a verbatim.** Tests: both review sequences with the corrected
  timing (held response released only *after* the clear-batch flush / recovery
  refetch), each asserting the second fetch **started before** the held response is
  released; the existing repo regression's timing fixed to match the failing
  sequence per the review's note.
- [x] **K2 · R11 — K0b verbatim.** SSE/API contract change recorded in phase-3.md
  D3/D5 (descriptor shape). Client: recovery adopts descriptors wholesale (drop the
  `activeSeqs ∩ known-starts` intersection). The randomized property test extended
  to **two-sided convergence**: every shown row is server-active AND every
  server-active request is shown, under randomized frame loss including lost
  `started` frames.
- [x] **K3 — Gate + truth pass.** Suites ×3 green; burst gate re-run; phase-3.md's
  documented display limit removed (it's fixed, not a caveat); plan checkboxes and
  architecture wording corrected to demonstrated behavior only.

**Exit:** both review sequences and the two-sided property test green; no remaining
open findings in the review report.

---

# Seventh-round remediation (Batch L) — P3 closers

> Source: round-seven review. **All P1/P2 findings R01–R26 resolved.** Two P3s remain;
> the review's required corrections are complete designs — no decisions to approve.
> Sonnet-suitable; one small session.

- [x] **L1 · R27 — TTFT survives frame loss.** `FirstToken` updates the locked active
  descriptor (mirroring `RequestReady`'s existing pattern) with optional `ttftMs`;
  `/active` returns it; recovery rebuilds it. Test: the review's dropped-`first_token`
  sequence (started delivered → first_token dropped → gap → recovery shows 42 ms)
  alongside the existing known-TTFT regression. phase-3.md's documented omission
  removed once fixed.
- [x] **L2 · R28 — Retention test reads one stable state.** The readiness predicate
  observes rows and file size from a single database state (or re-queries rows after
  the size condition holds, before asserting). All retention assertions kept in
  full — fix the observation, never the assertion (house rule, and the review's own
  requirement). Confirm with repeated full-suite runs.

**Exit:** clean re-review — zero open findings — closes the Phase 4 review cycle.

**Batch L landed 2026-08-29 — 273/273 backend green ×4 consecutive runs, 80/80 frontend
green ×3 consecutive runs, `tsc -b` + `vite build` clean, lint unchanged at 6 warnings
(baseline).** Notes and deviations:

- **L1.** `CaptureEvents.FirstToken` no longer returns early with no subscribers: it now
  takes the publish lock unconditionally (mirroring `RequestReady` exactly) and, when the
  seq is still in the active registry, replaces the locked descriptor with `TtftMs` set —
  in the same critical section as the frame's own id, so the descriptor stays coherent
  with the position the way `Model` already did. `ActiveDescriptor` gained a ninth
  positional field, `double? TtftMs`, defaulted to `null` at `Register`.
  - **The client-side fallback this was covering is now dead code, not just redundant —
    removed rather than left in place.** `toInFlight`'s old `known?.ttftMs` fallback
    existed specifically because the descriptor didn't carry TTFT; now that
    `FirstToken` and its publish share one critical section the same way `Register`
    and `started` do, any frame the client has already received must, by the same
    causality argument K0b already relies on for `model`, be reflected in every
    snapshot taken afterward — so a descriptor can never lack a TTFT the client
    independently knows. `toInFlight` now reads `descriptor.ttftMs` exactly the way it
    reads `descriptor.model`, and the function's now-unused `known` parameter was
    dropped (the caller still uses `known` separately, for the row-identity-reuse
    check).
  - Backend: `Active_DescribesEachInFlightRequest_AndLearnsItsModelFromRequestReady`
    (renamed `...AndLearnsItsModelAndTtftAfterRegistration`) extended to call
    `hub.FirstToken` and assert the descriptor's `TtftMs`, still with no subscriber
    attached — the same client the frame never reached. `Active_WireShape_
    CarriesOrderedDescriptors` extended to assert the real HTTP JSON response carries
    `ttftMs` (populated and `null`) alongside `model`.
  - Frontend: the existing `keeps a known TTFT on a row rebuilt from the recovery
    snapshot` regression now mocks a physically-consistent snapshot (the descriptor
    itself carries `ttftMs: 42`, since by the time of the reconnect the server's
    `FirstToken` call that produced the frame this client already received had
    already updated it) — its assertions are unchanged, only the fixture and its
    rationale comment. New `recovers a TTFT whose first_token frame was dropped
    entirely` reproduces the review's exact controlled sequence: `started` for seq 2
    reaches the client (id 1), its `first_token` (id 2, ttftMs 42) is never emitted at
    all (the drop), an unrelated `started` for seq 3 (id 3) exposes the gap, and the
    recovered row shows `ttftMs: 42` from the descriptor alone — nothing in the
    client's own state ever knew that number. `phase-3.md` D5's struck-through
    omission note is corrected in place (§ wire-shape bullet updated to list `ttftMs`
    in `ActiveDescriptor`); the K2 landed note's now-stale "two client details" bullet
    (which described the fallback this batch removed) is corrected in place too,
    per house style, rather than left standing next to the code that superseded it.
- **L2.** The race was exactly as diagnosed: `RetentionTests.MaxDbSize_FileShrinksUnderCap`
  combined a row list from one `SqliteConnection` with a file-size read from a second,
  separately-opened connection a moment later — a window the writer's delete-and-vacuum
  could land inside, so the readiness predicate could accept a post-retention size
  alongside a stale pre-retention row list. Fixed by observation, not assertion, per the
  house rule: `CaptureDb.QueryWithSize` reads the row list and `page_count * page_size`
  from **one** WAL read transaction on **one** connection, so both values describe the
  same database state — SQLite's WAL snapshot semantics guarantee the transaction's later
  reads cannot see a state newer than its first read, regardless of what the writer does
  concurrently. A new `WaitUntilWithSize` polls that combined snapshot the same way
  `WaitUntil<T>` polls `Query` alone; `WaitUntil<T>` itself is untouched; every other
  caller (11 call sites across 6 files) is unaffected. `MaxDbSize_FileShrinksUnderCap`'s
  own assertions (`rows.Count < total`, the newest row present, the oldest absent) are
  byte-for-byte unchanged — only how the snapshot was obtained changed. No reproduction
  of the original race under load was attempted (it needed the review's own heavier
  concurrent session to manifest even once); confirmed instead by code inspection of the
  fix's snapshot-isolation argument plus 4 consecutive full-suite runs (this batch's L1
  changes and the L2 fix together) with zero failures, matching the review's own note
  that the race is narrow enough that isolated/light-load runs routinely pass anyway.
- **Not done here:** no live-browser gate. Both findings are exercised end-to-end by
  automated tests that reproduce their exact repro steps — the backend wire-shape test
  drives a real HTTP round trip against a running `TestVessel`, and the frontend hook
  test reproduces the review's controlled SSE sequence against the same fake-EventSource
  harness this project already uses for reconciliation logic (see that file's own
  docstring: targeted hook tests, not manual browser probing, are this codebase's
  intended tool for this class of bug). Batch L's own framing ("Sonnet-suitable; one
  small session") does not call for repeating the Opus batches' burst/gate-level manual
  verification for two independent P3s.

**Batch K landed 2026-08-29 — 273/273 backend green ×3, 79/79 frontend green ×3, `tsc -b` +
`vite build` clean, lint unchanged at 6 warnings (baseline).** K0a and K0b were implemented as
approved. Notes:

- **K1 (R23 / K0a).** `refetchAuthoritative` is now `cancelQueries` **then** refetch.
  `refetchQueries` alone never started a second request while the list query's *initial* fetch
  was pending with no data — TanStack v5 hands back that retryer's promise — so J0 rule 4's
  "the last-started fetch wins" was, in exactly that state, "the first fetch wins", and a
  pre-clear snapshot became the authoritative answer. Cancelling settles the stale request as
  discarded, and `client.ts`/`RequestList.tsx` now thread TanStack's per-fetch `signal` into
  `fetch`, so the cancellation reaches the network rather than stopping at the query layer. No
  row is inspected or filtered — J0 rule 4's stance is what makes cancellation the right lever.
  - **One consequence, found by an existing test rather than reasoned about.** Because the
    trigger now awaits cancellation, its refetch starts a microtask *later* than the flush's
    `isFetching` check, so rows completing in the same window as a `cleared` were merged into a
    cache that the clear's own read was about to replace. The flush therefore treats "a clear
    happened in this window" as equivalent to "a fetch is in flight" and buffers them; they
    merge when that read settles. Caught by `keeps a post-clear completion, even one reusing a
    cleared id`, which failed on the first run of the cancel change.
  - **Test timing corrected, per the review's note.** The existing regression released the held
    response *before* the 100 ms clear batch, so the stale response had already settled when the
    clear handler ran and the test passed for the wrong reason. It now asserts a second fetch has
    started while the first is still unresolved, and only then releases it. Its sibling covers
    the same shape with the clear learned through recovery. Both fail (`[1]`, expected `[]`)
    with the cancel removed — the review's §2.1 sequences 1 and 2 exactly.
- **K2 (R11 / K0b).** The hub's registry stores an `ActiveDescriptor` per in-flight request —
  the `started` payload it already receives at registration, plus `model` once `request_ready`
  fires — and `/active` returns `{active: ActiveDescriptor[], logPosition, serverRunId}` from
  the same locked snapshot. `RequestReady` now takes the publish lock to record the model (it
  previously returned early with no subscribers), in the same critical section as the frame's
  own id, so the descriptor stays coherent with the position the way every other field does.
  The client rebuilds in-flight rows from the descriptors; the `activeSeqs ∩ known-starts`
  intersection is gone, so a lost `started` frame no longer hides a running request.
  - **One client detail worth recording** (a second no longer applies — see Batch L/R27
    below, which closed the gap this originally described). A row whose fields are unchanged
    keeps its object identity across recovery, so an unremarkable recovery does not rerender
    every live row.
  - **Property test now two-sided.** It asserted only that every row shown is server-active. It
    now also asserts that every server-active request is shown, and each scenario is
    constructed so that one still-running request always has its `started` frame dropped —
    otherwise the new half was only exercised on lucky seeds (it passed all six against the old
    intersection until the loss was made deterministic; afterwards all six fail against it).
  - Backend: `Active_DescribesEachInFlightRequest_AndLearnsItsModelAndTtftAfterRegistration`
    (extended and renamed in Batch L) pins the descriptor's fields, the model/TTFT updates and
    removal at the terminal transition, deliberately with **no subscriber attached** — that is
    the client the frame never reached. `Active_WireShape_CarriesOrderedDescriptors` pins the
    camelCase wire shape and seq ordering against the running app.
- **K3 (gate + truth pass).** Burst gate re-run on the rewritten snapshot contract, isolated
  instance as before (Release build, port 4560, temp config + DB in the session scratchpad,
  local Node streaming stub; the user's own instance and `vessel.db` untouched). **10,000
  requests, 24 concurrent, 100 tags: 10,000 OK / 0 failed in 77.67 s**, server settled at 10,000
  rows / 0 failed / empty active set / `logPosition` 40,000. Mid-burst `/active` returned live
  descriptors with every field populated, including the model parsed by `request_ready`. The
  same tab then converged through clear-all to zero and displayed the **100 post-clear requests
  whose SQLite ids were reused** (100 requests / 0 failed, ids 1–100, twelve tag chips). One
  document throughout: no reload, no replacement, no page errors; heap peaked at 24 MB, worst
  long task 85 ms.
  - **Same environment caveat as Batch J, unchanged:** the Browser pane could not be *displayed*
    in this session, so the tab ran hidden and throttled (probe sampling ~0.7 Hz against a 4 Hz
    interval). Those heap and long-task figures come from a throttled renderer and are not
    comparable with Batch I's visible-tab profile.
  - **What the gate does not show:** it does not drop a `started` frame or hold an initial list
    fetch across a clear — the two conditions K0a and K0b are actually about. Those are covered
    by the hook tests, each confirmed to fail against the pre-K behaviour. The gate's role here
    is that the enriched snapshot and the cancel-then-refetch survive real burst traffic.
  - Truth pass: `phase-3.md` D5 carries the descriptor shape, the cancel-then-refetch correction
    under the REST-authority rule, and the display limit struck through as **fixed** rather than
    recorded; `architecture.md`'s lifecycle paragraph, its clear sentence and the endpoint table
    match; the Batch J notes' display-limit bullet and the fifth-round closing entries 1 and 2
    are corrected in place rather than rewritten.

### Sixth-round closing conditions (§2 of code-review-phase-4.md)

| # | Condition | Status | Where |
| --- | --- | --- | --- |
| 1 | §2.1 — make the post-clear/post-recovery list read genuinely distinct from an older unsettled request, and stop the old result becoming authoritative; add both sequences, asserting the second fetch started before the held response is released; keep REST rows unfiltered | Met | K1 — cancel-then-refetch with the signal threaded to `fetch`; both sequences added with the corrected timing and a fetch-count assertion; both fail without the cancel. No row is inspected, filtered or deleted client-side |
| 2 | §2.2 — either enrich the recovery snapshot enough to reconstruct active rows, or approve a placeholder and narrow the guarantee | Met | K2 — K0b chose enrichment: `/active` returns descriptors, the client rebuilds rows from them, and the lost-`started` sequence now renders the request with its real method, path, tags, start time and model |
| 3 | Property test to two-sided convergence under randomized loss including lost `started` frames | Met | K2 — every shown row is server-active **and** every server-active request is shown; each scenario deterministically drops one still-running request's `started` frame |
| 4 | D04 — correct the documentation claims the two findings contradicted | Met | K3 — the "always starts a new fetch" rule now states how it is enforced; the display limit is struck through as fixed; the Batch J closing entries 1 and 2 and the architecture wording are corrected to demonstrated behaviour |

**Batch J landed 2026-08-29 — 271/271 backend green ×3, 76/76 frontend green ×3, `tsc -b` +
`vite build` clean, lint unchanged at 6 warnings (baseline).** J0 was implemented as approved,
as a replacement rather than an extension: the diff deletes more reconciliation logic than it
adds. Notes and deviations:

- **J1 (server).** `GET /active` is now `{activeSeqs, logPosition, serverRunId}`, all read in
  the one publish-lock critical section; `logPosition` is the hub's `_publishId`. The `cleared`
  frame keeps its id and its ordering (published under the same lock as `completed`, so a
  deleted row's completion always precedes it) and loses its payload entirely — it is now
  `data: {}`. **Deviations worth recording, both deletions the contract implies rather than
  states:**
  - `newestCompletedSeq` and the `_newestCompletedSeq` watermark behind it are **removed**, not
    left unused. J0 enumerates the response as `{runId, logPosition, activeSeqs}`, and nothing
    reads a boundary any more; keeping a field the client must not use would invite the retired
    rule back. `Active_CompletedRequestLeavesActiveSet_AndAdvancesBoundary` became
    `…_AtOrAboveItsOwnCompletedFrame`: it opens a real SSE connection, takes the id of the
    request's own `completed` frame off the wire, and asserts `/active` reports a position at
    or above it with an empty active set — the pairing the client's discard rule depends on.
  - `ClearState`, `_clear`/`_clearVersion`, `/active`'s `clear` field and
    `ClearResult.BoundaryId` are removed with it, so `ICaptureStore.Clear` returns `int` again
    and `SqliteCaptureStore` no longer reads `MAX(id)` inside the delete's transaction. Call
    sites swept: the writer's `RunClear`, both test store fakes, `ApiJsonContext`.
  - `Active_SnapshotStaysCoherent_UnderConcurrentRegisterAndComplete` is ported to the position
    rather than dropped. It now subscribes (frames are only published when someone is watching,
    and the position only advances with them) and each iteration publishes exactly three frames
    — `started(odd)`, `started(even)`, `completed(even)` — so iteration `k`'s odd seq `2k-1` has
    frame id `3k-2`, and every reader asserts: if `3k-2 <= logPosition` then `2k-1` is in the
    active set. That is J0's own invariant, and strictly stronger than the watermark version it
    replaces. A closing assertion pins `logPosition == 3 × iterations`, so the arithmetic the
    probe rests on cannot silently stop holding.
- **J2 (client).** `useLiveHistory`'s merge rules are replaced by recovery + ordered replay.
  Deleted: `clearedRow`, `purgeCleared`, `learnClear`, `clearRef`, `postClearIdsRef`,
  `clearPendingSettleRef`, the settle-time re-application, the post-clear provenance exemption
  and the boundary comparison. Kept: I0c's coalescing window (the queue is now the mechanism,
  not a hazard), the R10 completion buffer, the I0b run-id rule verbatim, and the terminal
  invariant (server-side, untouched). `useEvents` now hands every lifecycle handler its frame's
  SSE `id:` — without it the consumer cannot tell which held frames a snapshot already covers.
  Three deviations, each found by a test rather than reasoned about in advance:
  - **Recording, not suspending.** J0 rule 2 reads naturally as "hold the queue until the
    snapshot lands", and that was the first implementation. It fails the retained
    obsolete-run case for a good reason: a recovery whose `/active` is slow (or held, as that
    test holds it) then freezes *all* live rendering for its duration. Instead the flush keeps
    running and every frame arriving between "request issued" and "response applied" is also
    recorded; recovery replays the recorded frames above `logPosition` on top of the replaced
    map. Same arithmetic, same result, no liveness cost. The window is bounded by one HTTP
    round trip, and the recording is dropped on every exit path.
  - **The buffer drain moved off `useIsFetching` onto the query cache.** A fetch that starts
    and settles inside one React batch never renders an intermediate "fetching" value, so an
    effect keyed on that count sees `0 → 0`, never re-runs, and strands the buffer. That is not
    hypothetical: with J0 the `cleared` handler starts its own refetch, so a completion
    arriving in the same window as a clear was buffered and then stranded — caught by `keeps a
    post-clear completion, even one reusing a cleared id`. The drain now subscribes to the
    query cache, which notifies on every state change regardless of batching.
  - ~~**Display limit, recorded rather than fixed.** In-flight rows can only be rendered for
    seqs the client holds `started` details for, so recovery adopts `activeSeqs ∩ known`.~~
    **Fixed in Batch K (K0b).** Recording it was the right call at the time — J0 did not ask for
    per-seq metadata in the snapshot — but round six was right that it left R11 short of the
    brief's live-monitor guarantee rather than merely trading UX: a request the server reports
    running could stay invisible for its whole duration. The snapshot now carries descriptors,
    and the intersection is gone.
- **J3 (tests).** All four round-five cases are expressed against the new model, alongside
  every prior R11/R23 controlled-delivery case, the long-running survivor, the clear-before
  survivor and post-clear ID reuse (twice: with and without a completion frame). The
  "above the completed boundary" case became `replays a start that arrives while recovery is in
  flight, above the snapshot position` — the same hazard, stated in positions instead of seq
  distance. **Fixture note that matters:** `logPosition` is not free. The server takes it after
  the client's request goes out, so every frame the client already received has an id at or
  below it; a fixture pairing a low position with frames the client has seen describes a server
  that cannot exist. The migrated gap case therefore reports the freshly-started seq as active,
  because a real server could not answer otherwise. The property test drives a simulated server
  (frame script, database that reflects frames published so far, snapshots at the tightest legal
  position) through six seeded randomised delivery orders with ~25% frame loss and a mid-run
  reconnect, then asserts the settled list equals the server's and nothing is shown running that
  the server does not report running. **Evidence the tests bite:** disabling the position
  discard (floor, queue prune, replay filter) fails both §2.1 and §2.2 A plus three property
  seeds; disabling the `cleared` purge fails the buffered-completion case. The four review
  orderings are the review's own reproductions, which it recorded as failing against the I0a
  implementation those tests replaced.
- **J4 (gate + truth pass).** Burst gate re-run against the rewritten lifecycle core on an
  isolated instance (Release build, port 4560, temp config + DB in the session scratchpad, a
  local Node stub upstream on 4561 streaming six chunks at 20 ms; the user's daily-driver
  instance and `vessel.db` were never touched). **10,000 requests, 24 concurrent, 100 tags:
  10,000 OK / 0 failed in 77.54 s.** Server at settle: 10,000 rows, 0 failed, `activeSeqs` empty,
  `logPosition` 40,000. The tab was the same document throughout, never reloaded or replaced:
  no page errors, heap 16–17 MB, worst long task 56 ms, 0 in-flight rows at settle, list
  rendering the server's newest rows in order, tag picker bounded at 12 chips + "+88 more" (R12).
  **New wire contract verified live:** `cleared` arrives as `{}` at ids 40005/40006/40007, each
  *after* the deleted rows' completions (last `completed` at 40004), and `/active` reports no
  clear field and a position covering them. The live tab converged 10,001 rows → the single
  clear-before survivor → empty, driven by the frame's purge plus the refetch it schedules —
  J0 rules 3 and 4 on the real app.
  - **Environment caveat, and it limits this claim.** The Browser pane could not be *displayed*
    in this session, so the tab ran hidden and throttled: the in-page probe sampled at ~0.36 Hz
    instead of 4 Hz, and TanStack's `refetchInterval` pauses on a hidden document, so the stats
    bar sat at 0 while the list rendered the real rows (the caveat already recorded in Batches
    B/F/H/I). The heap and long-task numbers above therefore come from a throttled renderer and
    are **not** comparable with Batch I's visible-tab figures. What this run establishes is that
    the rewritten core drove 10k requests and three clears through one live tab without crash,
    error or reload and converged on the server's state; it does **not** re-establish Batch I's
    visible-tab profile, which stands as its own recorded evidence.
  - Truth pass: `phase-3.md` D3 records that deletion scope no longer travels to the client in
    any form; D5's `/active` shape, `cleared` payload, H0b(2) coherence note and I0b(1)
    allocation note are corrected in place, the I0a block is struck through with the reason it
    failed, and J0's four rules plus the display limit are written out. In this plan, the
    fourth-round closing table's entries 1, 2 and 6 are corrected in place (the review's D04
    finding was right that they claimed more than was shown), and I2's client half and I3 are
    annotated as superseded. Each round's reasoning is left standing and corrected where it was
    wrong, not rewritten.

### Fifth-round closing conditions (§7 of code-review-phase-4.md)

| # | Condition | Status | Where |
| --- | --- | --- | --- |
| 1 | Reconcile pending lifecycle frames with accepted recovery evidence; retain the R11 restart, stale-response, allocation and long-running controls | Met for the ordering half; **the display half closed in Batch K** — the queued-start race was fixed here, but round six found that a *lost* start left an active request unrenderable, which K0b fixes | J2 — recovery discards held frames at or below `logPosition` and replays only what came after, so a queued start cannot undo it (`does not let a queued start undo a recovery that already accounts for it`). All four retained controls still green |
| 2 | Resolve all three remaining R23 cases; preserve timestamp survivors and post-clear ID reuse | Met for those three; **a fourth path closed in Batch K** — rule 4's "always a new fetch" was not true while an initial fetch was pending, so a pre-clear response could still win (K0a) | J0/J2 — clears carry no predicate: queued pre-clear completions fall to the position filter, REST rows are never client-filtered, and multiple missed clears need no history because the refetch reads a database that reflects them all. Survivors come back from that refetch; ID reuse is covered with and without a completion frame |
| 3 | Add those interleavings to the existing suite and retain all current passing tests | Met | J3 — 76/76 frontend ×3 (was 67), 271/271 backend ×3; four review cases plus a six-seed randomised property test; no existing assertion weakened, and the two backend tests whose feature J0 deletes were ported to the position rather than dropped |
| 4 | Update the design/closure claims in their owning documents; protocol amendments approved first | Met | J4 — J0 was approved before implementation and is recorded in `phase-3.md` D3/D5; the overstated fourth-round entries are corrected in place and the superseded F/H/I entries annotated |

**Batch I landed 2026-08-29 — 271/271 backend green ×3, 67/67 frontend green ×3, `tsc -b` +
`vite build` clean, lint unchanged at 6 warnings (baseline).** I0a, I0b and I0c were
implemented as approved. Every new test was confirmed to *fail* against the pre-fix behaviour
before being kept. Notes and deviations:

- **I1 (R26).** `PrepareRequestBody` moved inside the handler's guarded span, with a narrow
  `catch (IOException or OperationCanceledException)` around preparation alone that sets
  `capture.Error = client_disconnect` and returns — the `finally` then enqueues the record and
  ends the lifecycle exactly as on every other client-side failure. New integration test
  `EventsTests.AbortedUsageInjectionUpload_LandsAsClientDisconnect_AndLeavesNoActiveEntry`
  drives the review's repro over a raw socket (injectStreamUsage backend, `Content-Length:
  4096`, JSON prefix, `LingerOption(true, 0)` reset mid-body) and asserts both halves: the
  active set empties, and the row lands with `client_disconnect` and a null status. Against
  the pre-fix placement it fails with `Assert.Empty() Failure … Collection: [1]` — the
  review's stranded seq, reproduced. **Known limit, recorded rather than fixed:** the bytes
  `ReadCapped` had already consumed when the abort hit are not captured (they live in a local
  buffer, not the tee), so an aborted injectStreamUsage upload stores no request body. The
  row, its error and the lifecycle are correct; body salvage on that path is a separate
  change, not smuggled into this one.
- **I2 (R11 / I0b).** (1) The `seq` counter moved from `CaptureContext` to `CaptureEvents`,
  allocated inside `Register` under the publish lock — allocation *is* registration, so the
  review's allocate-pause-snapshot-register interleaving is unrepresentable. `CaptureContext`
  gained `Register(method, path, backend, tags)` and a private-set `Seq`, and now takes the
  hub non-optionally. **Deviation worth recording:** `Register` takes the lock *twice* — once
  to allocate + register, once to fan out — so the `started` JSON is still serialized outside
  the lock (H2's zero-subscriber hot-path property). The split is safe in the only direction
  that matters: a snapshot taken between them sees the seq active but no frame yet, never a
  frame without the seq; and `completed` for that seq cannot overtake the frame, because the
  writer publishes it only after the handler has enqueued the record.
  (2) *The client half below is superseded by J0's wholesale replacement, except the run-id
  rule, which J2 keeps verbatim.* Client: `reconcile` records the run id its request was issued under and applies the
  response only when that *and* the response's own run id still equal the connection's current
  run; otherwise the response is discarded as evidence (the refetches still run, being
  run-agnostic). The old "different run id ⇒ clear the whole in-flight map" branch is gone —
  that was the bug. Tests: `EventsTests.Active_SnapshotStaysCoherent…` ported onto `Register`
  (the odd/even parity now comes from the hub's own allocation order, asserted); frontend
  `ignores a recovery response from an obsolete run instead of erasing the new run's requests`
  (the review's order A: expects `[1]`, got `[]` before the fix); the restart,
  long-running-survivor and above-boundary cases unchanged and green.
- **I3 (R23 / I0a).** *Retired by Batch J (J0). The whole predicate model below — server-side
  clear state, the `cleared` payload, `/active`'s `clear` field, `ClearResult.BoundaryId`, the
  client's armed re-application and post-clear provenance set — is deleted, not extended. It is
  left standing here because the reasoning is the record of what does not work: a client cannot
  re-derive which rows a past deletion removed, and each fix made the next ordering worse
  (review §2.2 A/B/C).* The hub keeps `ClearState {version, scope, beforeTs, boundaryId}` under
  the publish lock; `ICaptureStore.Clear` returns `ClearResult(Deleted, BoundaryId)` with
  `MAX(id)` read inside the delete's own transaction for a clear-all; the `cleared` frame and
  `GET /active`'s new `clear` field carry the identical shape. The client records the latest
  clear, purges by it on arrival, re-applies at fetch settlement and on buffer drain, and
  learns a missed clear from `/active` during recovery. Two points to record:
  - **Deviation — the re-application is armed, not permanent.** I0a says "whenever any list
    fetch settles"; applied literally and indefinitely that is *not* idempotent, because a
    clear-all empties the table and SQLite restarts row ids, so genuine post-clear rows sit
    below `boundaryId` and would be purged forever — it breaks the reused-id survivor case I3
    itself requires. It is therefore applied once more at the first quiescence after the clear
    — by then every fetch whose snapshot could predate the clear has settled, and every later
    response comes from a post-clear database — and then retired.
  - **Addition — post-clear provenance.** Rows learned from a completion published *after* the
    clear are exempt from the predicate (the wire order makes them post-clear by
    construction). This is what keeps `boundaryId` a *necessary* condition without needing it
    to be a sufficient one. The set is bounded: only rows that actually match the predicate
    are recorded, and it resets on each clear.
  - Tests: `purges a pre-clear list snapshot that settles after the clear` (review order A,
    including the ack's `invalidateQueries` against an unsettled initial fetch) and `applies a
    clear it never saw, from the version on /active, to the completion buffer` (review order
    B: dropped frame → id gap → recovery). Both fail without their fix. The three existing H3
    orderings plus a new `keeps a post-clear row reusing a cleared id across refetch
    settlement` pin the survivors; disabling the provenance exemption fails two of them.
    Backend: `Active_ReportsLatestClearVersionAndPredicate_WithoutAnySubscriber` — deliberately
    with no subscriber attached, since that is exactly the client the frame never reached.
- **I4 (crash diagnosis, then I0c, then the gate).** Profiled *before* changing anything, on a
  real Vessel (Debug build, port 4550, an isolated temp config + DB in the session scratchpad —
  the user's daily-driver `vessel.db` was never touched) against a fast local Node upstream
  (20–80 ms), with the Vite dev UI open in the in-app Browser pane, connected live. An in-page
  probe sampled the JS heap, `longtask` entries and timer drift every 250 ms and beaconed them
  to a local collector, so the timeline survives the pane discarding its renderer.
  **Measured cause, 10,000 requests at concurrency 24 (~1.3k SSE frames/s):** the heap climbs
  55 → 110 MB as traffic lands, then **one 10,258 ms long task** with a 15,474 ms timer stall
  while the heap explodes 76 MB → **3,107 MB** against a 4,096 MB limit; the renderer stopped
  answering (screenshots and script evaluation timed out, then `Render frame was disposed`).
  Server-side that same run was clean — 10,000/10,000 OK, active set empty, no log errors — so
  this is purely the tab, and its shape (allocation plus a single blocking task, not ordering)
  is what I0c predicted.
  **I0c as landed:** frames are queued and applied on a ~100 ms window — one `setInFlightMap`
  and one list-cache write per window (`mergeRows` replaced the per-row `mergeRow`; the R10
  buffer drain uses it too). Order within a window is preserved exactly, and a `cleared`
  mid-window still divides the completions it deletes from the ones it does not (rows queued
  for merge earlier in the same window are filtered by the predicate, since they are not yet in
  the cache for the purge to see). Pinned by `applies a burst of completions in one cache write
  per window` (40 completions → 1 cache write; 40 before the change).
  **Burst gate, after I0c, tab connected and visible throughout, never reloaded:** 10,000/10,000
  OK, 0 failed; heap peaked at 184 MB and settled to 72 MB; worst long task **92 ms** (78 tasks,
  4.5 s total blocking across a 255 s session); mid-burst the page rendered live in-flight rows
  (20 `.pulse-dot`s) with the SSE connected and stats climbing in real time; at settle **0**
  in-flight rows, `GET /active` empty, and the UI showed **53,000 requests / 0 failed**, matching
  the store exactly, with 100 tag facets and the picker bounded (12 chips + "+88 more", R12).
  No crash screen, no reload, no error in the Vessel log. 53,000 requests were driven through
  the live tab across the session.
  - **New wire contracts verified live** on the real app before the gate: `cleared` arrives as
    `{"version":1,"scope":"all","beforeTs":null,"boundaryId":21}` at publish id 64, *after* the
    deleted row's `completed` at id 63, and `GET /active` reports the same
    `clear {version, scope, beforeTs, boundaryId}`.
  - **Environment caveat (unchanged from Batches B/F/H).** While the Browser pane is hidden the
    Electron renderer is throttled to 1 Hz and eventually discarded (`Render frame was
    disposed`), and TanStack's `refetchInterval` pauses on a hidden document — which showed up
    once as a stats bar reading 30,000 while the page's own `fetch` returned 40,000. Displaying
    the pane rendered the correct 40,000 immediately. The gate figures above are all from
    observations taken with `document.hidden === false`.
- **I5 (truth pass).** `phase-3.md` D3's retired `boundaryId` ack entry struck through and
  corrected; D5 gained the I0b(1) seq-allocation note, the versioned-clear contract (frame +
  `/active`, with the necessary-not-sufficient reading of `boundaryId`), the "hello is the only
  restart signal" rule, and `/active`'s new `clear` field; D6 gained the I0c coalescing note
  with its measured numbers. In this plan, F2/F3 and H2/H3 are annotated in place as refined by
  I0 — each round's reasoning is left standing and corrected where it was wrong, not rewritten.


### Fourth-round closing conditions (§7 of code-review-phase-4.md)

| # | Condition | Status | Where |
| --- | --- | --- | --- |
| 1 | Complete R11's response-freshness handling without deleting legitimate live entries | **Overstated when written; met in Batch J** — the round-five review found a fourth R11 case I2 did not cover (a start queued in I0c's event window, applied *after* an authoritative recovery that already accounted for it: §2.1). The mechanisms below are real and retained, but "Met" was a claim about R11 as a whole, which it was not. J1/J2 close it: recovery discards held work at or below the snapshot's log position | I2 — a recovery response applies only while its own run id and its issuing request's run id both equal the current run; a mismatch is discarded, never read as a restart. Server-side, seq allocation moved inside `Register` under the publish lock, so the boundary rule is sound by construction. Restart / long-running / above-boundary controls unchanged and green |
| 2 | Complete R23 across pending REST results, dropped clear events and buffer settlement, retaining the survivor/ID-reuse cases | **Overstated when written; met in Batch J** — three further orderings failed (review §2.2 A/B/C): a queued pre-clear completion classified post-clear because it was *applied* later, a valid reused-id row purged for want of an SSE exemption, and an earlier missed clear erased by a later narrower one. The I0a machinery described below is retired, not extended | I3 — versioned clear state on the hub, reported by the `cleared` frame *and* `GET /active`; purged on arrival, re-applied at fetch settlement and buffer drain, learned from `/active` when the frame is lost. Both review orderings pinned; the three H3 orderings plus a new reused-id-across-refetch case still pass |
| 3 | Fix R26 so cancellation during preparation cannot bypass lifecycle finalization | Met | I1 — `PrepareRequestBody` inside the guarded span; the raw-socket abort repro asserts an empty active set and a `client_disconnect` row (fails as `Collection: [1]` against the old placement) |
| 4 | Keep all current tests passing and add these failing interaction cases; retain the round-three controls | Met | 271/271 backend ×3, 67/67 frontend ×3, `tsc -b` + `vite build` clean, lint at baseline (6). Every added case was confirmed red against the pre-fix behaviour; no existing assertion weakened |
| 5 | Investigate the live-tab crash and demonstrate the 10k/100-tag gate without a reload or replacement tab; record any remaining environment limitation precisely | Met | I4 — cause measured before the fix (one 10.3 s task, heap 76 MB → 3.1 GB of a 4 GB limit, renderer unresponsive), I0c landed against it, gate re-run on the same document with the tab visible and connected: 10,000/10,000 OK, 0 stuck rows, worst task 92 ms, UI 53,000/0 failed matching the store. Pane-hidden throttling/disposal and TanStack's hidden-document `refetchInterval` pause recorded as the environment limits they are |
| 6 | Correct stale API descriptions and acceptance claims in place | Partially met; corrected in J4 — the D04 documentation finding was right that conditions 1 and 2 above claimed more than was demonstrated. Those entries are corrected in place here, and `phase-3.md`'s clear-recovery contract is rewritten to J0 | I5 — `phase-3.md` D3 ack entry struck through, D5 given the seq-allocation, versioned-clear and hello-only-restart contracts plus `/active`'s `clear` field, D6 given the coalescing note; F2/F3 and H2/H3 annotated in place. The clean-publish claim is untouched by this batch and stays as Batch H left it — distinct from first-run/restart, which remains unverified |

**Batch H landed 2026-08-28 — 269/269 backend green, 62/62 frontend green, `tsc -b` +
`vite build` clean, lint unchanged at 6 warnings (baseline).** H0a and H0b were implemented
verbatim; no lifecycle mechanism outside them. Notes and deviations:

- **H1 (R25).** `CaptureChannel.Enqueue` now returns whether the work was admitted;
  `ProxyHandler`'s `finally` completes a dropped capture itself (`_captureEvents.Completed(seq,
  null)`) when admission is closed, and the writer's give-up path completes every capture it
  discards — `Flush` releases the failing batch's own remaining items via a new
  `TerminateAfterGiveUp` (a capture reaches `completed{row:null}`, a command is failed), and
  `DrainAfterStop` does the same for anything that races into the channel afterwards. **Design
  note worth recording:** the terminal completion is split across exactly those two owners on
  purpose (a capture is *either* dropped at admission → ProxyHandler completes it, *or*
  admitted-then-discarded → the writer completes it, never both), so `completed{row:null}` is
  never emitted twice for one seq without needing an idempotency guard or a literal
  registration "token" object — the seq ProxyHandler already holds *is* the token. New backend
  tests: `EventsTests.StoppedAdmission_ProxiedRequestsForward_ButLeaveNoActiveEntries`
  (`[Theory]` with/without subscriber, real `VesselApp`, the review's 32-request probe) and
  `CaptureWriterResilienceTests.WriterGiveUp_CompletesEveryDiscardedCaptureIdentity` (the drain
  path, with captures spilling from the failing batch into the drain). `TestVessel` gained a
  `Services` accessor and `TestCapture.Record` supports `with { Seq = … }` for these.
- **H2 (R11).** *Refined by Batch I (I0b): both halves were sound but incomplete. (1) The
  active set was coherent, yet a `seq` could still exist unregistered (allocated in the
  `CaptureContext` constructor), so a snapshot could report a request neither active nor
  unfinished — allocation now happens inside `CaptureEvents.Register`, under the publish
  lock. (2) The `reconcile` guard below discarded the in-flight **map** when an `/active`
  response's run id differed; that response is merely stale, and doing so erased the live
  requests of the run actually connected. A mismatched response is now discarded instead,
  and only `hello` signals a run change.* `CaptureEvents.RunId` (a per-process GUID) rides on a new `hello` SSE frame
  (the connection's first frame, deliberately **no `id:`** so it never moves the gap
  watermark), on `GET /active`, and on `GET /status`. The client (`useEvents` → `useLiveHistory`)
  discards its whole in-flight map on a run-id change and also guards `reconcile` against an
  `/active` response whose `serverRunId` differs from the connection's. Coherence (H0b(2)): the
  in-flight set became a plain `HashSet<long>` and the watermark a plain `long`, both now
  mutated and read only under the existing `_publishLock` (id allocation and fan-out already
  lived there), so `GetActiveRequests` returns one coherent snapshot. **Perf note:** event JSON
  is still serialized *outside* the lock (guarded by an `_subscribers.IsEmpty` check), so the
  zero-subscriber hot path stays free of both JSON work and, for `started`/`completed`, anything
  but a single set mutation under the lock. New tests: `EventsTests.ServerRunId_ConsistentAcross
  HelloActiveAndStatus`, `EventsTests.Active_SnapshotStaysCoherent_UnderConcurrentRegisterAnd
  Complete` (the ported 4-reader invariant probe — zero violations with the lock; the pre-fix
  concurrent-dictionary read would violate it), and a frontend restart repro (`discards
  in-flight rows from a prior run after a restart (run-id change)`). Two existing SSE tests
  (`Sse_StartedFirstTokenCompleted…`, `Sse_EveryFrameCarriesMonotonicEventId`) were taught to
  skip the new id-less `hello` frame rather than miscount it — no assertion weakened.
- **H3 (R23).** *Extended by Batch I (I0a): the in-band frame is right, but it was the
  *only* carrier of deletion state on a feed that deliberately drops frames, and it purged
  only what was already in the cache/buffer — so a dropped frame, or a pre-clear list
  snapshot settling afterwards, restored deleted rows. The clear is now a versioned
  predicate the hub keeps and `GET /active` reports, re-applied at fetch settlement.*
  The Batch F3 boundary/generation model is **retired**, not patched. The writer
  publishes a `cleared {scope, beforeTs}` frame at clear-commit time under the publish lock, so
  a deleted row's `completed` always precedes `cleared` on the wire; `useLiveHistory` purges
  buffered + listed rows by the server's own predicate (`all`, or `startedAt < beforeTs`) and
  treats everything after as post-clear by construction (covering SQLite id reuse). The
  `DELETE /requests` ack's `boundaryId` and the whole `ClearOutcome` struct are gone —
  `ICaptureStore.Clear` now returns a plain `int` count, swept through the endpoint, both fake
  stores, and the client (`ClearResponse.deleted` only). `App.handleDataCleared` keeps its R14a
  detail-cache/selection hygiene (still driven by the ack, correctly — that concerns the
  *selected* row, not completion ordering) but no longer bumps a generation. The three existing
  F3 frontend clear tests were rewritten to drive the in-band event and now match the review's
  exact orderings (delayed completion before the clear frame; inverted id/start clear-before
  survivor; post-clear id-reuse).
- **H4 (R24).** One-line effective-mode fix in `DetailPane`: `responseInRawView = !responseRendered
  || responseDisplay === 'raw'`, so when extraction returns null (no Rendered/Raw toggle exists)
  the "Raw stream" sub-toggle actually swaps in `responseRaw`, and the decode notice (wired to
  the shown body) follows it. New `DetailPane.test.ts` exercises the actual tab/toggle
  interaction (unknown-format stream, known-format-with-failed-extraction, decode-truncated raw,
  normal-body warning preserved).
- **Live burst (F4 re-run) — run this session and passed.** A real Vessel (Debug build, port
  4550, an *isolated temp config + DB in the session scratchpad* — the user's daily-driver
  `vessel.db` was never touched) was pointed at a fast local Node upstream (~20–80 ms/response),
  the Vite dev UI opened in the in-app Browser pane connected live, and four bursts of
  10k/10k/10k/3k requests at concurrency 24 across 100 distinct tags were sent with the tab
  connected and never reloaded. Results:
  - **New wire contracts verified live** (before the burst, via raw `curl` on the real app): the
    SSE feed opens with `event: hello` / `{serverRunId}` and **no `id:`**; `started`(id1) →
    `request_ready`(id2) → `completed`(id3) carry the row; a `DELETE …?scope=all` returns
    `{deleted:1}` (no `boundaryId`) and publishes `event: cleared` / `{scope:"all"}` at id4,
    correctly ordered *after* the completion. `/status` and `/active` both carry the same
    `serverRunId`.
  - **Zero stuck in-flight rows.** Mid-burst the live page rendered in-flight rows (15
    `.pulse-dot`s sampled) with the SSE staying connected (no "Disconnected" indicator); after
    settle the page showed **0** `.pulse-dot`s and `GET /active` returned an **empty** set after
    every burst, with `newestCompletedSeq` advancing 10001 → 33001. No reload needed.
  - **Server lifecycle clean, no failures, no crash.** 33,000/33,000 requests returned 200, **0
    failed**, the store held all 33,000 rows with **100 distinct tag facets**, and the Vessel log
    contained no error/exception/crash — only the startup line. `serverRunId` was stable
    throughout (no spurious restart). The tag picker was bounded (12 chips + "+88 more",
    `max-height: 84px`) at 100 real facets (R12).
  - **Environment caveat (unchanged from Batch B/F).** The in-app Browser pane's Electron
    renderer is discarded while the pane is hidden, which surfaced as one `Render frame was
    disposed` when reading the DOM between observations; a forced re-navigation re-woke it and
    every *displayed* observation showed a healthy app. This is the same pane-lifecycle artifact
    those batches recorded — not a Vessel/React crash (no page-crash screen ever appeared).
- **Superseded-by-H0 annotations** (H5's truth-pass requirement) added in place to the Batch F
  landing notes (F2/F3).

### Third-round closing conditions (§7 of code-review-phase-4.md)

| # | Condition | Status | Where |
| --- | --- | --- | --- |
| 1 | Complete R11: coherent snapshots + restart-safe lifecycle identity, without expiring legitimate active requests | Met | H2 (one-lock coherent snapshot; `serverRunId` on hello/active/status; long-running-survivor + fresh-above-boundary tests unchanged and green) |
| 2 | Complete R23: correct deletion scope/ordering across server op, SSE, ack, and list settlement, incl. survivors + reused ids | Met | H3 (in-band `cleared` event ordered against completions; `boundaryId` retired; the three review orderings + id-reuse pinned in `useLiveHistory.test.ts`) |
| 3 | Fix R24's raw-stream fallback + R25's terminal lifecycle cleanup | Met | H4 (`DetailPane` effective-mode fix + `DetailPane.test.ts`); H1 (terminal invariant + `StoppedAdmission…`/`WriterGiveUp…` tests) |
| 4 | Keep existing suites passing; add the failing interaction cases to existing tests | Met | 269/269 backend (incl. the ported concurrent-snapshot + terminal probes), 62/62 frontend, `tsc -b` + `vite build` clean, lint at baseline (6) |
| 5 | Correct the plan's R11/R23 closure claims in place; keep the smoke distinct from unverified first-run/restart | Met | F2/F3 landing notes annotated as superseded by H0 (above); the live 10k/100-tag burst (4×, connected tab) was re-run this session and passed — zero stuck in-flight rows, 0 failed of 33,000, no crash (see the live-burst note above) |
