# Code review through Phase 4 — round-two verification

**Review date:** 28 August 2026

**Reviewed remediation:** `14c5c5e` — `Code review feedback round 2`

**Previous remediation:** `b4c5d63` — `Code review feedback`

**Original review baseline:** `3db797a`

**Scope:** verify previous findings against the current code, brief, architecture,
Phase 0–4 specifications, plan, and accepted remediation decisions. Future phases are
not acceptance requirements for this review.

## Assessment

**The findings are not all fixed.** Four of the six findings outstanding after the first
re-review are now resolved: **R05, R09, R18 and R22**. The original off-page recovery and
buffer-before-clear examples also pass, but **R11 and R23 remain partially resolved**
under other valid event orderings. Two regressions need correction: **R24**, which hides
raw response streams, and **R25**, which retains active-request entries after capture
stops. There are **four current P2 findings**, detailed in §2.

The repository's backend suite passed **264/264 in three consecutive runs**, without
failures or skips. The frontend suite passed **56/56**. The production build and current
clean-publish smoke passed. Separate probes using the production frontend hook/components
produced **two passing controls and five failing assertions**. Backend probes additionally
confirmed inconsistent lifecycle snapshots, incorrect clear-before boundary assumptions,
and retained active entries after admission stops.

The live 10,000-request exercise did not reproduce the previous browser crash. The same
tab eventually showed stored history and usable 100-tag filters without a manual reload,
with no running rows observed after settlement. There was a visible backlog, delayed
facets, and a later **Disconnected** indicator. This is positive evidence, but not proof
of uninterrupted live operation or a root cause for the old crash.

**Phase 4 should not be signed off while the four findings remain.** The overall
architecture remains appropriate; the defects concern lifecycle consistency, deletion
ordering across interfaces, and the raw-view fallback.

This review changes only this report. No application code or repository tests were
modified, and no commit was made. A concurrent edit to
[plan.md](E:/Code/Vessel/docs/plan.md) added future replay decisions; it was left untouched
and is outside this review's scope. Earlier report versions remain in Git history.

## 1. Status of prior findings

“Resolved” refers to the reported defect and the stated verification, not every possible
failure of the surrounding feature. Findings closed in round one were checked against
the current changes and rerun suites; their earlier interactive checks were not all
repeated. Of R01–R23, **21 are resolved and two remain partial**; R24–R25 are new below.

| ID | Status | Current evidence and qualification |
| --- | --- | --- |
| **R01 — Clean publish omits UI** | **Resolved** | A clean source copy with no build outputs or node_modules produced a 102.9 MB win-x64 executable. Resource inspection, executable launch, SPA/JS serving and proxy assertions passed. The current smoke has narrower startup coverage; see §5. |
| **R02 — Stale live-config routing** | **Resolved** | Atomic ConfigSnapshot publication and request-scoped backend resolution remain intact. Concurrency tests passed in all three runs. Architecture §9.1 now describes the implemented model. |
| **R03 — Markdown makes automatic requests** | **Resolved** | Inert captured URLs, explicit embedded-image previews and control-plane CSP remain. MessageView and CSP tests pass; generate images reuse the same preview policy. |
| **R04 — Settings steals input focus** | **Resolved** | The stable dialog lifecycle and scoped clock remain unchanged. Prior interactive focus/confirmation evidence still applies; not independently repeated this round. |
| **R05 — Unbounded decoding / invisible read-time truncation** | **Resolved; related regression R24** | Bounded decoding remains covered. The body flag is now mirrored and displayed. A 4 MiB gzip capture reopened with a 1 MiB cap showed a body-local warning despite an untruncated summary. R24 separately breaks raw-stream selection. |
| **R06 — Writer stops but queue accepts forever** | **Resolved for original queue defect** | Admission closes and commands fail fast; resilience tests pass. R25 is a new leak in the active registry, not a recurrence of the retained-body queue. |
| **R07 — Clear overtakes queued captures** | **Resolved** | Writer FIFO and atomic clear/FTS tests pass. R23 concerns client merge semantics after the correctly ordered clear. |
| **R08 — Transport error loses partial enrichment** | **Resolved** | Partial-response and pre-response-error integration tests pass; upstream content provenance remains intact. |
| **R09 — Ollama tools/thinking lost** | **Resolved** | Generate now accumulates top-level thinking as well as chat thinking/tools. Adapter/golden and renderer tests pass. An independent probe retains thinking in reassembly and searchable text. |
| **R10 — Completion during initial fetch disappears** | **Resolved for original fetch race** | Buffered-completion tests pass. Clear ordering remains separately tracked as R23. |
| **R11 — Lost events leave running rows** | **Partial** | The off-page case now passes against the active-request endpoint. Restart identity and snapshot consistency remain defective; see §2.1. |
| **R12 — Tags hide history list** | **Resolved** | At 1280×720, the 100-tag picker stayed at 84 px and history retained a 385 px viewport, collapsed and expanded. Tag 050 selected correctly. Delayed facet availability is qualified in §5. |
| **R13 — Backend rename overwrites another** | **Resolved** | Case-insensitive collision validation remains unchanged; prior browser verification still applies. No save was made against the user's configuration. |
| **R14 — Clear leaves stale detail identity** | **Resolved for original same-tab selection case** | Detail eviction/selection clearing remain; the clear callback now precedes list invalidation. The accepted cross-tab SQLite ID-reuse caveat is unchanged. R23 is a separate same-tab list defect. |
| **R15 — Null config sections return 500** | **Resolved** | Loader and PUT validation tests pass, preserving existing config on invalid input. |
| **R16 — Repeated save loses restart warning** | **Resolved** | Bound-listener comparison and GET restart state remain; repeated-save/listen-change tests pass. |
| **R17 — Malformed messages blank app** | **Resolved** | Normalized-view validation and local error boundaries remain; component tests pass. R24 breaks raw-stream selection without removing the error boundary. |
| **R18 — Image preview absent** | **Resolved** | Generate extracts top-level images, including image-only requests, into the captured-image preview. Generate malformed-entry and MessageView preview tests pass. |
| **R19 — SSE EOF treated as blank line** | **Resolved** | Terminal LF/CRLF and incomplete-stream parser/adapter cases pass. |
| **R20 — Intermittent stats assertion** | **Resolved for reported rounding failure** | Deterministic and explicit-tolerance assertions remain. Three full runs passed; no tests were skipped, weakened or rerun to conceal a failure. |
| **R21 — Failed save destroys valid config** | **Resolved** | Atomic replacement and failure-preservation tests pass. This does not claim power-loss durability. |
| **R22 — Concurrent SSE IDs arrive out of order** | **Resolved** | Allocation and fan-out share the publish lock; the client never rewinds its watermark. Concurrent-publisher, genuine-drop and coalesced-recovery tests pass. Snapshot consistency is a distinct R11 issue. |
| **R23 — Completions restore cleared history** | **Partial** | The exact buffer-before-clear case passes. Receipt-time generations and maximum deleted ID cannot describe all valid clear/SSE orderings; see §2.2. |

## 2. Remaining code findings

### 2.1 R11 — P2: Active-state recovery is not consistent across snapshots or restart

**Locations:** [CaptureEvents.cs:82](E:/Code/Vessel/src/Vessel/Capture/CaptureEvents.cs:82),
[CaptureContext.cs:21](E:/Code/Vessel/src/Vessel/Capture/CaptureContext.cs:21),
[useLiveHistory.ts:171](E:/Code/Vessel/frontend/src/api/useLiveHistory.ts:171),
[useEvents.ts:56](E:/Code/Vessel/frontend/src/api/useEvents.ts:56).

The endpoint fixes the previous dependence on loaded history pages. However, its
`activeSeqs` and `newestCompletedSeq` are not one coherent snapshot, and its process-local
sequence numbers do not identify which server process supplied them.

**Reproduction A — restart:** deliver `started(seq=100)`, then reconnect after a restart
with `{ activeSeqs: [], newestCompletedSeq: 0 }`. The production hook retains request 100:
it only removes absent sequences at or below the new completed boundary. The external
regression assertion fails. CaptureContext's static counter resets with the process;
resetting the SSE ID watermark does not reset the hook's map. Without further traffic,
the old request remains running indefinitely. Phase 4's reconnect carry-in explicitly
includes a Vessel restart.

**Reproduction B — concurrent snapshot:** GetActiveRequests copies active keys, then
separately reads the completed watermark. Between those reads, another request can
register and a later sequence can finish. The returned watermark can therefore cover a
still-running request missing from the returned key array.

A probe repeatedly registered an odd sequence that never completes, then registered and
completed the following even sequence. A consistent snapshot must contain every odd
sequence at or below its watermark. **187 of 571 snapshots violated that invariant**;
one reported watermark 464 but contained only 230 of the 232 required active odd entries.
All omitted odd requests were still running. If the corresponding started frame reaches
the client before that API response, reconciliation wrongly removes a legitimate request.
The publish lock does not cover these state reads/writes.

**Required outcome:** provide a coherent lifecycle snapshot and a boundary that can be
compared safely with incoming events, including an explicit way to distinguish server
lifetimes. Preserve legitimate long requests while removing abandoned prior-process
entries. Add restart and concurrent-snapshot cases to the existing lifecycle tests;
serial active-set assertions and the passing off-page control do not establish this.

### 2.2 R23 — P2: Clear boundaries can resurrect deleted rows or discard survivors

**Locations:** [useLiveHistory.ts:151](E:/Code/Vessel/frontend/src/api/useLiveHistory.ts:151),
[useLiveHistory.ts:284](E:/Code/Vessel/frontend/src/api/useLiveHistory.ts:284),
[useLiveHistory.ts:301](E:/Code/Vessel/frontend/src/api/useLiveHistory.ts:301),
[DataPanel.tsx:36](E:/Code/Vessel/frontend/src/components/DataPanel.tsx:36),
[SqliteCaptureStore.cs:370](E:/Code/Vessel/src/Vessel/Storage/SqliteCaptureStore.cs:370).

The local generation advances when the DELETE response reaches the browser; completions
are stamped when their SSE frames reach the browser. Those independent response streams
need not arrive in database-operation order. Clear-before also deletes by started_at,
whereas the client treats MAX(deleted id) as a deleted prefix. IDs follow persistence
order, not request start time.

Three external tests using the real hook and QueryClient fail:

| Valid ordering | Expected | Actual |
| --- | --- | --- |
| Clear commits; its response and empty list refresh arrive; a delayed completion for deleted row 1 then arrives over SSE. | List stays empty. | Row 1 is merged back. It has the current receipt generation, or bypasses the buffer entirely. |
| Initial list snapshot is pending; clear commits; a new surviving row completes before the DELETE acknowledgment; that acknowledgment advances the generation; the old snapshot settles empty. | New row remains visible. | Its buffered completion is discarded as pre-clear. This also applies when SQLite reuses a deleted ID. |
| A newer fast request is persisted as ID 1; an older slow request becomes ID 2; clear-before deletes only ID 2 while both completions are buffered. | Surviving ID 1 is merged. | Both are dropped because both IDs are at or below boundary 2. |

The third ordering was also checked through the real API: an older slow stream and a
newer fast request produced IDs 2 and 1 respectively. DELETE returned
`{ deleted: 1, boundaryId: 2 }`; the subsequent database-backed list correctly contained
**ID 1**. The server clear was correct; the client's inferred predicate was not.

**Required outcome:** clear scope and completion identity/order must be defined by the
server operation, not the order of browser callbacks. Respect the timestamp predicate
for clear-before and preserve post-clear captures. Cover both sides of the acknowledgment,
delayed SSE, initial fetch/refetch settlement, out-of-order completion and accepted ID
reuse. Retaining the current predicate longer cannot fix its ordering or scope assumptions.

This follows the remediation plan's maximum-ID approach, so that plan needs a design
correction too. No schema migration or alternative protocol is selected by this report;
choose and approve the correction before implementation.

### 2.3 R24 — P2: Raw stream selection fails without a rendered response

**New regression in the decode-warning wiring.**

**Location:** [DetailPane.tsx:80](E:/Code/Vessel/frontend/src/components/DetailPane.tsx:80).

`responseBodyShown` selects responseRaw only when both responseDisplay and responseView
are raw. But responseDisplay defaults to rendered, and its toggle only exists when
responseRendered exists. When extraction returns null, the fallback appears without
changing that state. Clicking **Raw stream** changes only responseView; the body remains
responseBody instead of responseRaw.

**Reproduction:** capture unknown-format streamed NDJSON containing three JSON lines.
The detail API returns streamed=true, responseBody=null, and those lines in responseRaw.
Open Response and select Raw stream. The actual browser still shows **No response body**.
A separate DetailPane test likewise finds no rendered body after the click. Before this
change, the fallback selected the stream independently of rendered/raw display state.

This breaks the brief's graceful raw fallback and
[Phase 4 D4](E:/Code/Vessel/docs/phase-4.md:102), which keeps raw inspection available
regardless of format. It also prevents the new warning from following a selected raw
stream in this branch.

**Required outcome:** resolve the effective fallback mode when choosing the displayed
body and its warning. Test unknown-format streams and known formats whose extraction
fails, including a decode-truncated raw payload. Preserve the passing normal-body warning.

### 2.4 R25 — P2: Stopped capture retains every later request in the active registry

**New regression introduced by the server active-request set.**

**Locations:** [CaptureEvents.cs:97](E:/Code/Vessel/src/Vessel/Capture/CaptureEvents.cs:97),
[ProxyHandler.cs:129](E:/Code/Vessel/src/Vessel/Proxy/ProxyHandler.cs:129),
[CaptureChannel.cs:44](E:/Code/Vessel/src/Vessel/Capture/CaptureChannel.cs:44),
[CaptureWriterService.cs:120](E:/Code/Vessel/src/Vessel/Capture/CaptureWriterService.cs:120).

Every proxied request registers in _active, even without SSE subscribers. Removal depends
on CaptureEvents.Completed, normally called by the writer. Once admission stops,
CaptureChannel.Enqueue drops captures without a terminal lifecycle notification. The
writer's stop-drain path also releases commands without completing discarded capture
identities.

**Reproduction:** run the real app with a temporary config/database and local stub, then
call CaptureChannel.Stop to enter the production terminal admission state. Send 32 normal
proxy requests. All return HTTP 200, but the active snapshot retains all **32 sequences**,
and the completed watermark stays unchanged. The probe ran without an SSE subscriber.
It exercises stopped admission directly; it did not induce five actual disk failures.

Forwarding continues as intended, but every subsequent finished request adds permanent
registry state. The viewer shows false running requests; reconciliation cannot repair
them because the authority says they are active. Memory grows with traffic even when
the viewer is closed. R06's body-queue fix does not release this new dictionary state.

**Required outcome:** every registered lifecycle must reach a terminal transition when
a capture is persisted, dropped, rejected, or drained after shutdown/failure. Keep
forwarding independent of capture health. Extend writer/admission tests to assert
lifecycle cleanup with and without subscribers, including records racing stop.

## 3. Brief, architecture and plan alignment

The accepted decisions in
[code-review-phase-4-plan.md](E:/Code/Vessel/docs/code-review-phase-4-plan.md:15)
remain the contract. This review does not reopen approved product choices.

| Decision | Current assessment |
| --- | --- |
| **D01 — Wire versus decoded storage** | Resolved. Wire bytes remain stored and scratch decoding bounded; the read-time warning now exists. R24 separately blocks selecting some raw streams. |
| **D02 — Non-streamed non-Ollama tok/s** | Resolved. Unsupported whole-request estimates remain absent; exact Ollama evaluation-duration throughput is preserved. |
| **D03 — Host/browser-origin policy** | Implemented and covered by passing guard tests. It is not authentication or permission to expose the app publicly. |
| **D04 — Documentation/acceptance accuracy** | **Partial.** Architecture §9.1 is corrected, and the plan records burst runs and smoke changes. Its [closing table](E:/Code/Vessel/docs/code-review-phase-4-plan.md:913) still declares R11 and R23 fully met; those claims must be revised in place given the reproductions above. |
| **D05 — In-flight filters** | Session-only scoping and filtered-view counts remain as approved. Their correctness still depends on R11/R25 lifecycle state. |
| **R14b — SQLite ID reuse** | Accepted caveat remains. R23 requires correct same-tab behavior without assuming IDs can never be reused. |

The implementation retains the intended YARP forwarding, capture tees, off-path
enrichment, single SQLite writer, parameterized history/FTS, provider adapters and embedded
React UI. Round two improves publication ordering and provider fidelity without changing
that direction. The remaining bugs do not justify replacing the architecture.

Replay, diff, copy-as-curl, pricing, live token tailing, release CI, the platform release
matrix and the Phase 6 bind-address banner remain future work, not Phase 4 findings.
Concurrent future replay additions to plan.md were not implemented or reviewed here.

## 4. Coverage and engineering assessment

The new concurrent-publisher test, coalesced recovery tests, off-page control, clear-buffer
cases, generate fixtures and decode-warning tests cover the original failures usefully.
The gaps are at interaction boundaries:

- Lifecycle tests need simultaneous mutation/snapshot reads and a process restart, not
  only sequential calls using one sequence namespace.
- Clear tests need server operation order separated from HTTP/SSE delivery order, and
  persistence order separated from start-time order.
- Warning tests need the actual DetailPane fallback/toggle interaction, not just the
  notice component or a body that already renders.
- Stopped-writer tests must assert registry cleanup as well as channel admission,
  retained bodies and waiting commands.

Extend the existing suites at these seams. External failing probes were retained and
were not skipped or changed to accept the defects. The seven frontend probes include
passing controls for off-page recovery and the newly visible normal-body warning.

Lint completed with **six warnings**, in RequestList, App, DetailPane and ConfigPanel.
The build also reports a **502.33 kB minified JS chunk**, above the 500 kB warning
threshold. These are not additional demonstrated correctness findings; neither command
should be described as warning-free.

## 5. Verification actually performed

### Automated checks

| Check | Result |
| --- | --- |
| `dotnet test --solution Vessel.sln` | Three consecutive workspace runs: **264 passed, 0 failed, 0 skipped** each; 12.228, 11.869 and 11.773 seconds. |
| `npm test` | **56 passed** across eight repository test files. |
| `npm run build` | TypeScript and production build passed; chunk-size warning noted above. |
| `npm run lint` | Completed with six warnings. |
| External frontend regressions | **2 controls passed, 5 assertions failed**: restart recovery, three clear orderings, raw-stream selection. Production hook/components and real QueryClient; event/API delivery is controlled. |
| C# / real API probes | Clear-before survivor below maximum deleted ID; 32 retained active entries after stop; 187 inconsistent snapshots out of 571; generate thinking retained. Diagnostic probes, not additional passing repository tests. |
| Decode + browser | A 4 MiB gzip capture reopened under a 1 MiB cap displays 1,048,576 text characters and the decode-limit alert; summary truncation remains false. |

### Clean publish and executable

The current smoke passed using a clean source copy containing 326 source files and no
pre-existing dist/bin/obj/node_modules. It built the frontend before backend compilation
and produced a **102.9 MB** self-contained win-x64 executable. Embedded resources,
status/backend listing, intact proxy path/body, unknown-backend 404, SPA shell and hashed
JS asset checks passed.

An external copy preserved every current smoke assertion. Only source-root selection
and cleanup were adjusted to retain evidence. The default port was not used.

**Coverage qualification:** round two changed the smoke from two launches with a reused
directory to one launch with a prewritten config and fresh database on an ephemeral port.
The current smoke passes, but it does **not** independently exercise first-run config
creation or same-database restart. Config creation has a passing unit test; the prior
SQLite restart I/O error was neither reproduced nor root-caused by this one-launch test.
Avoiding that sequence is not proof that restarting against an existing database is
fixed or verified.

### Live browser and 10,000-request exercise

Browser checks used the clean source's real VesselApp and embedded production UI with
a temporary database/config and local stub, separate from the executable smoke.
**10,000 synthetic requests at concurrency 24** cycled 100 tags and five models.
Submission took **3.584 seconds**; retention left **10,000 stored rows**, zero failures,
and 100 tag facets. This local exercise is not a production throughput claim.

The tab was open before the burst and was not manually reloaded afterwards. At the first
post-burst observation, visible requests still had running timers around 11 seconds and
the header had its old count. Later observations showed 10,000 requests and no visible
running rows; the active endpoint returned an empty set with completed boundary 10002.
Facets populated later still. The same tab then expanded all 100 tags and filtered tag
050. At 1280×720, collapsed and expanded states retained a **385 px history viewport**
and an **84 px tag region**.

No crash screen was observed. A **Disconnected** indicator appeared during later
observations while the host remained available. Recovery latency and the disconnect
were not traced. This supports eventual history recovery and usable layout without a
reload, not an uninterrupted connection, an immediate-settlement performance guarantee,
or a causal explanation for the earlier crash. The independent code reproductions,
rather than an inferred crash cause, determine the verdict.

Seven serial API samples after the burst:

| Operation | Median | Maximum |
| --- | ---: | ---: |
| First history page | 1.60 ms | 3.66 ms |
| FTS search for needle | 28.92 ms | 30.75 ms |
| Exact tag 050 | 15.26 ms | 17.87 ms |
| Model + successful status | 1.85 ms | 3.70 ms |
| Facets, including 100 tags | 53.78 ms | 57.70 ms |
| All-session statistics | 12.77 ms | 13.57 ms |

A real-model multi-turn/tool session and the literal under-ten-second search litmus were
not independently repeated. Checked-in fixtures are regression evidence, not substitutes
for those manual acceptance exercises. The isolated host and stub were stopped after
testing; the user's running instance and database were not modified.

## 6. Evidence retained locally

All probe sources, synthetic databases/configs and logs are outside the repository.

- Backend suites: [run 1](C:/Users/spenc/AppData/Local/Temp/vessel-phase4-round2-review/backend-tests-1.log),
  [run 2](C:/Users/spenc/AppData/Local/Temp/vessel-phase4-round2-review/backend-tests-2.log),
  [run 3](C:/Users/spenc/AppData/Local/Temp/vessel-phase4-round2-review/backend-tests-3.log).
- Frontend: [tests](C:/Users/spenc/AppData/Local/Temp/vessel-phase4-round2-review/frontend-tests.log),
  [build](C:/Users/spenc/AppData/Local/Temp/vessel-phase4-round2-review/frontend-build.log),
  [lint](C:/Users/spenc/AppData/Local/Temp/vessel-phase4-round2-review/frontend-lint.log).
- [Frontend probe output](C:/Users/spenc/AppData/Local/Temp/vessel-phase4-round2-review/frontend-probes-final.log),
  [hook source](C:/Users/spenc/AppData/Local/Temp/vessel-clean-c0819a64eb1a4b1f92d78b43bd62ba5d/frontend/src/api/round2-review.test.ts),
  [DetailPane source](C:/Users/spenc/AppData/Local/Temp/vessel-clean-c0819a64eb1a4b1f92d78b43bd62ba5d/frontend/src/components/round2-detail-review.test.ts).
- [C# probe source](C:/Users/spenc/AppData/Local/Temp/vessel-phase4-round2-review/probes/Program.cs)
  and [results](C:/Users/spenc/AppData/Local/Temp/vessel-phase4-round2-review/backend-probes.log).
- [Publish log](C:/Users/spenc/AppData/Local/Temp/vessel-phase4-round2-review/publish.log),
  [decode/raw API cases](C:/Users/spenc/AppData/Local/Temp/vessel-phase4-round2-review/ui-cases.log),
  [10k seed/timings](C:/Users/spenc/AppData/Local/Temp/vessel-phase4-round2-review/seed.log).

These temporary paths are evidence, not project dependencies. Section 2 describes each
failure sufficiently to transfer its regression into the existing repository suites.

## 7. Conditions for closing this review

1. Complete R11: coherent snapshots and restart-safe lifecycle identity, without expiring
   legitimate active requests.
2. Complete R23: correct deletion scope and ordering across server operations, SSE,
   acknowledgments and list settlement, including survivors and reused IDs.
3. Fix R24's raw-stream fallback selection/warning and R25's terminal lifecycle cleanup.
4. Keep existing suites passing and add the failing interaction cases to existing tests.
   Re-run the live gate and record connection/recovery behavior explicitly.
5. Correct the remediation plan's R11/R23 closure claims in place. Keep the successful
   current smoke distinct from unverified first-run/restart behavior; do not claim a root
   cause for the earlier browser crash without a trace.

No application fixes or new product/design decisions are implemented by this report.
