# Code review through Phase 4 — round-four verification

Date: 29 August 2026 (Europe/London)

Reviewed revision: `99d0041` — `Code review feedback round 4`

Comparison: `57edf94..99d0041`, plus the current implementation, tests and approved
Batch I decisions. Working tree was clean before review.

Scope: verify the fixes for R11, R23 and R26; check their interactions with event
batching; repeat the live-browser gate; retain earlier closures where the current
diff and regression checks give no reason to reopen them. This replaces the previous
report's current assessment in place.

## Assessment

**Not yet clear: R11 and R23 remain partial.** The specific round-three reproductions
are fixed, including the upload-abort leak. However, four additional delivery cases
fail consistently: one lifecycle case and three clear-state cases. These remain
under the existing finding IDs, not four new findings.

**24 of R01–R26 are resolved; two remain partial.** There are two current P2 code
findings, detailed in §2.

The live-tab gate now passes independently: the same visible tab handled **10,000
streamed requests across 100 tags without a reload, replacement or crash**. The final
UI and server both reported 10,000 requests and zero failures, with no server-active
requests remaining.

Verification:

- Backend: **271/271 passed in three consecutive complete runs**, zero skips.
- Frontend: **67/67 passed in two complete runs**.
- Additional review probes: **3 passed, 4 failed**, with identical results on repeat.
- Clean single-file publish, embedded UI/resource checks and proxy smoke: passed.
- Lint: six existing warnings; production build: successful, with the existing
  bundle-size warning.

Only this report was changed in the repository. No implementation/test changes or
commits were made. Independent probes and logs are retained outside the repository.

## 1. Closure status

### Findings under active verification

| ID | Status | Current evidence |
| --- | --- | --- |
| **R11 — Lifecycle recovery** | **Partial** | Atomic allocation/registration and stale-run response rejection fix the previous cases. The independent snapshot probe found 0 inconsistencies in 658 snapshots. Recovery still ignores lifecycle frames waiting in the new event queue; §2.1. |
| **R23 — Clear ordering/recovery** | **Partial** | The original stale-initial-response and dropped-clear/buffer cases now pass. Recovery can still misclassify queued completions, purge valid REST rows with reused IDs, or forget an earlier missed clear; §2.2. |
| **R26 — Upload abort bypasses finalization** | **Resolved** | The real TCP abort probe registered request 1, then observed an empty active set and a persisted `client_disconnect` row with null status. A subsequent control request returned HTTP 200 and persisted normally. |
| R24 — Raw stream unavailable in fallback | Resolved | The current DetailPane regression tests pass. The implementation is unchanged in this round; the previous embedded-UI verification remains applicable. |
| R25 — Active registry grows after capture stops | Resolved for the reported admission/drain paths | The repeated independent stopped-admission control returned 32/32 HTTP 200s and zero active entries. Writer give-up/drain tests also pass. |
| Live-tab crash / 10k gate | Passed in this review | Same-tab production-asset workload completed without navigation or recovery by replacement; details in §5. |

R26's closure concerns terminal lifecycle and error capture. The remediation plan
explicitly records that bytes already consumed into the injection reader's local
buffer are not salvaged after an interrupted upload. This review does not claim that
partial-body salvage was implemented.

### Earlier findings

These closures are carried forward after inspection of the round-four diff and the
current complete suites. Earlier manual security/configuration exercises were not
all repeated.

| ID | Status | Qualification |
| --- | --- | --- |
| R01 — Clean publish omits UI | Resolved | Clean publish/resource/SPA/asset/proxy smoke passed again. |
| R02 — Stale config routing | Resolved | Request-scoped configuration/routing remains; concurrency tests pass. |
| R03 — Captured Markdown makes requests | Resolved | Inert URLs, captured-image previews and CSP remain; guard/component tests pass. |
| R04 — Settings focus loss | Resolved | Stable dialog lifecycle and scoped clocks remain; earlier interactive evidence carried forward. |
| R05 — Decode allocation / invisible truncation | Resolved | Bounded decoding and body-local notices remain covered. |
| R06 — Queue accepts after writer stops | Resolved | Closed admission and command failure behavior remain covered. |
| R07 — Clear overtakes queued captures | Resolved | FIFO writer/clear ordering passes; R23 concerns client reconciliation after that operation. |
| R08 — Partial response enrichment lost | Resolved | Interruption/provenance integration tests pass. |
| R09 — Ollama thinking/tools lost | Resolved | Adapter, golden and renderer tests pass. |
| R10 — Initial fetch loses completions | Resolved for the original completion race | Buffering remains effective; its deletion interactions are R23. |
| R12 — Tags hide list | Resolved | The same live tab retained usable list space with 100 tags; post-burst 1280×720 measurement below. |
| R13 — Backend rename collision | Resolved | Guard unchanged; previous interactive verification carried forward. |
| R14 — Clear leaves stale selected detail | Resolved for the original same-tab selection case | Ack-driven detail eviction remains. Accepted cross-tab ID reuse is not reopened. |
| R15 — Null config causes 500 | Resolved | Validation/preservation tests pass. |
| R16 — Restart warning disappears | Resolved | Bound-listener/repeated-save tests pass. |
| R17 — Malformed render blanks app | Resolved | Validation and local boundaries pass; raw fallback remains available. |
| R18 — Image preview missing | Resolved | Extraction and captured-image preview tests pass. |
| R19 — SSE EOF counted as blank line | Resolved | Parser/adapter terminal and incomplete-stream cases pass. |
| R20 — Intermittent stats rounding assertion | Resolved for the reported failure | Three more complete runs passed; deterministic fixtures/tolerances remain. |
| R21 — Failed save destroys config | Resolved | Atomic replacement/preservation tests pass; no power-loss durability claim. |
| R22 — SSE publication IDs reorder | Resolved | Ordered fan-out and gap-coalescing controls pass. Ordered SSE alone does not order REST responses against deferred UI work. |

## 2. Remaining code findings

### 2.1 R11 — P2: A queued start can undo an authoritative recovery

Locations: [recovery updates the applied map](E:/Code/Vessel/frontend/src/api/useLiveHistory.ts:297),
[the later batch applies queued starts](E:/Code/Vessel/frontend/src/api/useLiveHistory.ts:377).

The allocation/registration fix is correct: constructing a context no longer reserves
a sequence, and registration assigns it while adding it to the active set under the
hub lock. The obsolete-run response also no longer erases the current run's entries.

The new batching layer introduces another state holder which recovery does not
reconcile: `eventQueueRef`.

Reproduced sequence:

1. A reconnect recovery is pending.
2. `started(seq=1)` reaches the client and waits in the 100 ms event queue.
3. The server finishes request 1; its completion frame is lost.
4. The pending recovery returns `activeSeqs=[]`, `newestCompletedSeq=1` for the
   current run before the queued start is applied.
5. Recovery reconciles an empty applied map. The batch subsequently inserts seq 1.

**Actual:** in-flight set `[1]` after settlement. **Expected:** `[]`, since the
accepted server snapshot already proves that request finished. The test waits beyond
both the batching and debounce windows and fails identically twice.

This can reintroduce a completed request as running until a later completion/recovery
corrects it. It is a controlled event-loss/recovery reproduction, not a claim that
the ordinary 10k browser run exhibited stuck rows.

**Required correction:** make accepted recovery evidence apply to pending lifecycle
work as well as the rendered map. Preserve the existing protections for a genuinely
active request, a start above the snapshot boundary, and an obsolete run. Keep batching;
do not restore per-frame rendering to avoid this ordering problem.

### 2.2 R23 — P2: Clear recovery still lacks sufficient ordering and identity

Locations: [learning/purging a clear](E:/Code/Vessel/frontend/src/api/useLiveHistory.ts:243),
[settlement purge](E:/Code/Vessel/frontend/src/api/useLiveHistory.ts:263),
[post-clear exemption](E:/Code/Vessel/frontend/src/api/useLiveHistory.ts:448),
[server retains only the latest clear](E:/Code/Vessel/src/Vessel/Capture/CaptureEvents.cs:263).

The previous two failures now pass: a pre-clear initial response is purged after it
settles, and recovery removes a previously buffered completion when its clear frame
was dropped. The timestamp predicate and the observed post-clear ID-reuse controls
also remain green.

Three additional cases fail:

#### A. Recovery overtakes a queued pre-clear completion

An already-pending recovery returns a clear after `completed(row=1)` has arrived,
but before its 100 ms batch flush. The clear frame itself was lost.

`learnClear` purges the list and `pendingRef`, but not the event queue. When that
queue flushes, the completion sees a non-null `clearRef` and is classified as
post-clear merely because it is being **applied** later. It is added to
`postClearIdsRef` and merged after the authoritative empty refetch.

**Actual:** row `[1]` returns. **Expected:** `[]`.

The comment that this completion must have been published after the known clear is
only valid for ordered processing of the SSE frames. It is not valid when the clear
was learned independently through REST.

#### B. A valid post-clear REST row is removed when its SSE completion is missing

A list request is outstanding when clear-all deletes the old row with ID 1. A new
row then reuses ID 1. The outstanding request takes its database snapshot after that
insert and returns the new row; the new row's completion event is lost.

Because a fetch was outstanding, the final purge is armed. Without an SSE completion
to establish its exemption, the valid row is removed by `id <= boundaryId`.

**Actual:** `[]`. **Expected:** the new row `[1]`.

An outstanding HTTP request does not prove its database snapshot predates the clear.
Retiring the predicate after settlement prevents later refetch damage, but does not
make this first settlement safe. Correct REST recovery cannot depend on receipt of
the completion frame it is meant to recover from.

#### C. The latest clear can erase knowledge of an earlier missed clear

Row 1 is buffered while a list fetch is pending. Clear-all v1 deletes it, but that
frame is lost. A later clear-before v2 uses an earlier cutoff and deletes nothing.
The client receives v2 and recovery also returns v2, because the hub overwrote v1.

Neither v2's timestamp predicate nor its version describes v1's deletion. When the
list returns empty, row 1 is restored from the completion buffer.

**Actual:** `[1]`. **Expected:** `[]`.

Monotonic version numbers identify that state changed; they do not preserve all
missed deletion effects. A later narrower predicate does not subsume an earlier one.

**Required correction:** recovery must distinguish stale rows from live rows without
requiring an SSE exemption, order queued completions against recovered clear state,
and recover the effects of multiple missed clears. These are partly design gaps in
I0a's latest-predicate/provenance model. Agree the amended recovery contract before
implementation; a snapshot/barrier or explicit generation/deletion identity would
need to cover all three cases, including SQLite ID reuse and clear-before survivors.

## 3. Brief, architecture, plan and engineering assessment

The implementation remains aligned with the core project structure: YARP forwarding,
bounded capture, a background SQLite writer, adapter enrichment and an embedded
React UI. No replacement of those components is indicated by this review.

The [approved Batch I decisions](E:/Code/Vessel/docs/code-review-phase-4-plan.md:1020)
were used as the current design authority alongside the brief, architecture and
Phase 0–4 specs:

| Area | Assessment |
| --- | --- |
| I1 / R26 terminal lifecycle | Implemented and independently verified through real HTTP cancellation. |
| I0b allocation and response identity | Both requested mechanisms work. The later event queue must participate in reconciliation too. |
| I0a recoverable clears | Implemented for the specified single-clear cases, but the approved assumptions are insufficient for §2.2. |
| I0c event coalescing | The throughput gate passes independently; preserve this improvement while fixing ordering. |
| D01, D02, D03, D05 | Earlier decisions remain applicable: wire fidelity/bounded decode, supported metrics, local-origin guard and session-only in-flight scoping. Passing tests do not make the local guard authentication or a public-hosting guarantee. |
| D04 documentation accuracy | Improved, but still partial: R11/R23 closure claims exceed the behavior demonstrated. |

The former contradictory DELETE-ack description has been marked retired and the
current contracts describe registration, clear state and hello-only restart handling.
The live-crash investigation is now recorded separately from the successful gate;
this review also achieved the gate without re-navigation.

However, the [fourth-round closing table](E:/Code/Vessel/docs/code-review-phase-4-plan.md:1171)
still marks R11 and R23 complete. Correct those entries and their associated guarantees
in place when addressing §2. The [clear recovery contract](E:/Code/Vessel/docs/phase-3.md:228)
also needs to describe the amended behavior rather than claim the current latest-only
state recovers every missed clear.

The remaining testing gap is the composition of three independently ordered paths:
SSE arrival, deferred batch application and REST completion. Existing tests mostly
flush one path before asserting the next. Extend the existing hook suite with the four
retained interleavings, not just additional happy-path batching counts.

No failing test was skipped, weakened or removed. The external probes use the production
hook, a real QueryClient and the existing fixture style, with valid ISO timestamps.
They wait beyond the coalescing window. The old allocation-before-registration probe
was adapted to the new public API: the former state is now unrepresentable, and the
replacement asserts that the delayed registration receives a sequence above the prior
snapshot's boundary. The original probe source remains retained.

Future replay, diff, copy-as-curl, cost estimates, Ollama panels and the release platform
matrix remain outside Phase 4. No adjacent implementation or unapproved design change
was made.

## 4. Verification performed

| Check | Result |
| --- | --- |
| `dotnet test --solution Vessel.sln` | 271 passed, 0 failed, 0 skipped ×3; durations 17.160 s, 16.714 s, 17.904 s. |
| `npm test -- --run` | 67 passed ×2; 9 test files. |
| `npm run lint` | Exit 0; six existing warnings. |
| Independent frontend probes | 3 passed / 4 failed ×2. Passing: prior stale-run response, stale initial list, dropped clear with already-buffered completion. Failing: the four cases in §2. |
| Real interrupted upload | Active request removed; `client_disconnect` persisted with null status; control request HTTP 200. |
| Stopped-admission control | 32/32 HTTP 200; active set empty. |
| Allocation/registration control | Paused context seq 0; snapshot watermark 1; later registration seq 2, active and above that watermark. |
| Concurrent snapshot control | 658 snapshots, 0 inconsistencies during 40,000 registrations / 20,000 completions. |
| Clean publish smoke | Passed: fresh source copy, frontend build, self-contained single-file win-x64 executable, embedded resources, status, proxy path/body, unknown-backend 404, SPA and hashed JS asset. |

The clean copy contained 327 source files and initially no dist/bin/obj/node_modules.
The smoke script's assertions were retained; its external copy retained temporary
artifacts rather than recursively deleting them. The executable used a fresh config,
temporary database and ephemeral port, never the user's default-port instance.

The published executable was approximately **103 MB**. The minified JS bundle was
**504.08 kB** and triggered Vite's 500 kB warning. Build success is not a warning-free
build. No new release-wide performance or vulnerability-audit claim is made.

First-run default-config creation, restart persistence, live-key remote providers and
the multi-platform release matrix were not independently re-exercised in this pass.
The clean smoke uses a prewritten config; it is not evidence for those other gates.

## 5. Live browser gate

A separate VesselApp host served the production frontend assets built in the clean
copy, using a temporary SQLite database and local stub. No Vite development server was
used. Review tab 7 was visible before seeding and remained the same document throughout.

Workload: **10,000 requests, 24 concurrent, 100 tags**, streamed stub responses with
20 ms delays between chunks. All requests returned successfully in **26.24 seconds**.

Observed:

- During the burst, the UI showed advancing counts and live rows; one snapshot showed
  4,736 completed requests, another 6,656.
- After settlement, UI and API both showed **10,000 requests, 0 failures**.
  `/active` returned `activeSeqs=[]` and `newestCompletedSeq=10000`.
- No crash, reload, replacement tab or navigation was needed.
- The 100-tag picker expanded, and selecting `review-tag-050` worked.
- At a post-burst **1280×720** viewport, expanded tags occupied **84 px** and the
  filtered list retained **382 px** of height. The viewport override was then reset.
- Search `needle 9950` plus that tag produced one row; its Request tab displayed
  `Synthetic phase four search needle 9950`.
- The browser's captured warning/error log was empty.

Seven-sample local API medians were 1.74 ms for the list, 24.56 ms for FTS,
14.12 ms for the tag filter, 44.78 ms for facets and 11.58 ms for stats. These are
synthetic local observations, not production benchmarks. An initial model timing
sample used an incomplete model name and returned zero rows; it is excluded here.
A separate exact-model request for `qwen2.5:1.5b` returned 100 rows.

The natural visible viewport during the workload was 1550×1216. This review did not
collect a new heap/long-task profile or repeat the pre-fix crash. The remediation
plan's profiling numbers remain its own recorded evidence; the independent conclusion
here is that the same-tab workload and subsequent interactions passed.

The temporary review host was stopped and the review tab closed. The user's existing
Vessel instance and data were not modified.

## 6. Retained evidence

[Evidence index and reproduction commands](C:/Users/spenc/AppData/Local/Temp/vessel-phase4-round4-review/README.md)

[Focused frontend test source](C:/Users/spenc/AppData/Local/Temp/vessel-clean-46249e7d7d5545d592a72cca30b05aee/frontend/src/api/round4-review.test.ts)

[First probe results](C:/Users/spenc/AppData/Local/Temp/vessel-phase4-round4-review/frontend-probes-1.log)
and [identical repeat](C:/Users/spenc/AppData/Local/Temp/vessel-phase4-round4-review/frontend-probes-2.log)

[Backend probes](C:/Users/spenc/AppData/Local/Temp/vessel-phase4-round4-review/backend-probes.log),
[publish log](C:/Users/spenc/AppData/Local/Temp/vessel-phase4-round4-review/publish.log),
[live workload log](C:/Users/spenc/AppData/Local/Temp/vessel-phase4-round4-review/seed.log)
and [browser observations](C:/Users/spenc/AppData/Local/Temp/vessel-phase4-round4-review/browser-observations.md)

These are local temporary artifacts, not committed fixtures or portable CI outputs.

## 7. Conditions for closing this review

1. Reconcile pending lifecycle frames with accepted recovery evidence; retain the
   now-passing R11 restart, stale-response, allocation and long-running controls.
2. Resolve all three remaining R23 cases: queued pre-clear completions, valid REST
   rows without SSE provenance, and multiple missed clears. Preserve timestamp
   survivors and post-clear ID reuse.
3. Add those interleavings to the existing suite and retain all current passing tests.
4. Update the design/closure claims in their owning documents to match the verified
   behavior. Any protocol/design amendment needs approval before implementation.

R26 and the uninterrupted live-tab gate no longer block closure. **R11 and R23 do.**
