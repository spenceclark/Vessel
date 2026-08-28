# Code review through Phase 4 — round-three verification

**Review date:** 28 August 2026

**Reviewed remediation:** `57edf94` — `Core review feedback round 3`

**Previous remediation:** `14c5c5e` — `Code review feedback round 2`

**Original review baseline:** `3db797a`

**Scope:** verify the four outstanding findings against the implementation, brief,
architecture, Phase 0–4 contracts and approved Batch H decisions. Check the changed paths
for regressions; future phases remain outside scope.

## Assessment

**The findings are not all closed.** R24's raw-stream fallback and R25's stopped-admission
leak are fixed. The specific restart, torn-snapshot and clear-order examples from round
two also pass, but **R11 and R23 remain partial** under additional valid delivery
orderings. A newly identified cancellation path bypasses lifecycle finalization:
**R26**. There are **three current P2 code findings**, detailed below.

The backend suite passed **269/269 in three consecutive runs**, without failures or
skips. The frontend suite passed **62/62**. The production build and current clean-publish
smoke passed. Seven independent frontend probes produced **three passing controls and
four failing assertions**, with the same result in a second run. A real HTTP upload-abort
probe confirmed R26.

**The uninterrupted live-view gate did not pass.** All 10,000 streamed requests completed,
were persisted, and left the server active set empty. The original browser tab reached
a state titled **“This page crashed.”** A fresh tab loaded history and the 100-tag layout
correctly. No crash dump or causal trace was obtained; this is not attributed to a
particular application defect or dismissed as a browser-host artifact.

Do not sign off Phase 4 on the current evidence. The architecture remains appropriate;
the unresolved issues concern applying asynchronous snapshots, recovering deletion state,
and completing every registered request lifecycle.

Only this report was changed in the repository. Application code and repository tests
were not edited; failing external probes were retained. No commit was made. The working
tree was clean at the start of this review, and previous report versions remain in Git.

## 1. Closure status

### Findings outstanding after round two

| ID | Status | Verification |
| --- | --- | --- |
| **R11 — Lifecycle recovery** | **Partial** | A new-run hello removes old running entries; server snapshot reads/writes now share one lock. The prior invariant probe recorded **0 inconsistent snapshots out of 606**, versus 187/571 previously. However, stale API responses can still remove legitimate live requests; §2.1. |
| **R23 — Clear ordering** | **Partial** | The maximum-ID/client-generation design is retired. Ordered cleared events and timestamp predicates fix the three earlier delivery examples, including inverted start/ID order and reused IDs. Pending REST responses and loss of the clear frame remain unsafe; §2.2. |
| **R24 — Raw stream unavailable in fallback** | **Resolved** | Effective raw mode now includes failed/absent extraction. The previous external DetailPane assertion passes; repository tests cover stream selection and its decode warning. In the embedded production UI, selecting Raw stream displayed all three captured NDJSON lines. |
| **R25 — Active registry grows after capture stops** | **Resolved for the reported stop/admission/drain paths** | Admission returns success/failure; ProxyHandler completes rejected captures and the writer completes discarded records. Existing tests cover subscribers/no subscribers and give-up cleanup. The independent 32-request control returned 32 HTTP 200s and **zero active entries**. R26 is a different, newly identified exit before the finalizer. |

### Earlier findings

These closures are carried forward after inspecting the round-three diff and running the
full current suites. Earlier manual UI/security exercises were not all repeated.

| ID | Status | Current qualification |
| --- | --- | --- |
| R01 — Clean publish omits UI | Resolved | Clean executable/resource/SPA/JS/proxy smoke passed again; startup coverage limits in §5. |
| R02 — Stale config routing | Resolved | Atomic snapshot and request-scoped routing remain; concurrency tests pass. |
| R03 — Captured Markdown makes requests | Resolved | Inert URLs, explicit embedded-image previews and CSP remain; component/guard tests pass. |
| R04 — Settings focus loss | Resolved | Stable dialog lifecycle and scoped clock remain unchanged; prior browser evidence carried forward. |
| R05 — Decode allocation / invisible truncation | Resolved | Bounded decoding and body-local notices remain covered; R24's related display regression is now fixed. |
| R06 — Queue accepts after writer stops | Resolved | Closed admission and command failure behavior remain covered; original retained-body queue defect does not recur. |
| R07 — Clear overtakes queued captures | Resolved | FIFO writer/clear tests pass; R23 is client-state consistency after that server operation. |
| R08 — Partial response enrichment lost | Resolved | Interruption/provenance integration tests pass. |
| R09 — Ollama thinking/tools lost | Resolved | Chat/generate adapter, golden and renderer coverage remains passing. |
| R10 — Initial fetch loses completions | Resolved for original completion race | Buffering still preserves arriving completions. Its interaction with deletion is tracked as R23. |
| R12 — Tags hide list | Resolved | Fresh-tab 100-tag layout retains a 409 px list viewport at 1280×720; see live-gate qualification below. |
| R13 — Backend rename collision | Resolved | Collision guard unchanged; prior interactive verification carried forward. |
| R14 — Clear leaves stale selected detail | Resolved for original same-tab selection case | Ack-driven detail eviction remains. Accepted cross-tab SQLite ID reuse is not reopened. |
| R15 — Null config causes 500 | Resolved | Validation and preservation tests pass. |
| R16 — Restart warning disappears | Resolved | Bound-listener/repeated-save tests pass. |
| R17 — Malformed render blanks app | Resolved | Normalized validation and local boundaries pass; raw fallback remains accessible. |
| R18 — Image preview missing | Resolved | Chat/generate extraction and captured-image preview tests pass. |
| R19 — SSE EOF counted as blank line | Resolved | Parser/adapter terminal and incomplete-stream cases pass. |
| R20 — Intermittent stats rounding assertion | Resolved for reported failure | Three consecutive complete runs passed; explicit tolerances and deterministic fixtures remain. |
| R21 — Failed save destroys config | Resolved | Atomic replacement/preservation tests pass; no power-loss durability claim. |
| R22 — SSE publication IDs reorder | Resolved | Ordered fan-out and non-rewinding/coalesced client recovery remain; concurrent/drop tests pass. |

Of R01–R25, **23 are resolved and two remain partial**. R26 adds one newly identified
code finding; the untraced browser crash is a separate acceptance failure.

## 2. Remaining code findings

### 2.1 R11 — P2: Stale recovery responses can erase legitimate live requests

**Locations:** [useLiveHistory.ts:158](E:/Code/Vessel/frontend/src/api/useLiveHistory.ts:158),
[useLiveHistory.ts:165](E:/Code/Vessel/frontend/src/api/useLiveHistory.ts:165),
[ProxyHandler.cs:75](E:/Code/Vessel/src/Vessel/Proxy/ProxyHandler.cs:75),
[CaptureEvents.cs:103](E:/Code/Vessel/src/Vessel/Capture/CaptureEvents.cs:103).

The server snapshot is now internally coherent, and normal restart detection works.
Neither establishes that an asynchronously arriving snapshot is safe to apply to the
client's *current* entries. Two controlled-delivery tests fail:

**A — response from an obsolete run removes the new run's requests.**

1. Run A's recovery request is pending.
2. A hello for run B arrives; the hook correctly clears A's entries.
3. A started event for B's request 1 arrives and is displayed.
4. The pending response from A resolves.
5. Its run ID differs from the current hello, so reconciliation clears the **whole
   current map**, including B's request 1.

The assertion expects [1], but receives []. A subsequent correct run-B snapshot reports
request 1 active, yet does not restore it: reconciliation only removes entries. A stale
response must not be treated as evidence that the currently connected run has restarted.

**B — a valid snapshot predates a lower-sequence started event.**

Sequence allocation happens when CaptureContext is constructed, before the call to
CaptureEvents.Started. Request 1's handler can pause between those operations while
request 2 starts and finishes. A coherent snapshot then contains no active entries and
completed boundary 2. Request 1 subsequently registers; its SSE frame can reach the
browser before that snapshot's HTTP response.

Applying the snapshot deletes request 1 because it is absent and 1 ≤ 2, despite it being
active now. The corresponding hook assertion again expects [1] but receives [].

A separate probe using production CaptureContext and CaptureEvents confirms the server
ordering is representable: it allocated sequences 35 and 36, completed 36, took the empty
snapshot with boundary 36, then registered 35. The current active set contained 35 while
the earlier snapshot did not. This is a controlled scheduling reproduction, not a claim
that the live burst independently exhibited this exact interleaving.

**Required outcome:** distinguish obsolete responses from a new server lifetime, and do
not apply an old snapshot to lifecycle entries created after its boundary. Request
sequence magnitude alone is not a reliable registration/publication boundary. Preserve
the now-working restart/coherence controls and add both delivery-order regressions to
the existing hook tests.

### 2.2 R23 — P2: Clear recovery still allows old REST results or buffers to restore rows

**Locations:** [useLiveHistory.ts:139](E:/Code/Vessel/frontend/src/api/useLiveHistory.ts:139),
[useLiveHistory.ts:180](E:/Code/Vessel/frontend/src/api/useLiveHistory.ts:180),
[useLiveHistory.ts:229](E:/Code/Vessel/frontend/src/api/useLiveHistory.ts:229),
[DataPanel.tsx:41](E:/Code/Vessel/frontend/src/components/DataPanel.tsx:41).

Ordered SSE solves the old ambiguity between completions and the DELETE acknowledgment.
However, applyCleared only removes rows present in the cache/buffer **at that moment**.
It does not guard pending REST results, and recovery after a lost frame does not reconcile
the completion buffer against deletion state.

**A — a pre-clear initial REST snapshot resolves after clear and acknowledgment.**

Start the initial list request and hold its response, whose database snapshot contains
row 1. Deliver cleared(all), then perform the successful DELETE acknowledgment's list
invalidation while that initial request is pending. Resolve the held response with row 1.

The list ends as **[1], not []**. There was no cached row to purge when cleared arrived.
TanStack Query reuses an initial in-flight request with no data rather than replacing it
merely because invalidateQueries was called; the installed query implementation and the
real QueryClient probe both confirm this. No further event is needed for the stale row
to remain displayed.

**B — the cleared frame is dropped, and gap recovery remerges an invalid buffer.**

Hold the initial list fetch. Receive completed(row 1), which is buffered. Clear row 1
on the server, but omit the cleared frame as the bounded drop-oldest queue may do.
Deliver a later event with an ID gap. Recovery correctly calls the active endpoint and
refetches history; resolve history with an authoritative empty page.

When fetching settles, the untouched completion buffer merges row 1 back into that
empty page. The result is again **[1], not []**. Both cases include the same list
invalidation used by the initiating DataPanel, so they are not merely the accepted
other-tab ID-reuse caveat.

Both assertions failed in two runs. Existing clear tests resolve pending lists empty
and always deliver the clear frame; those assumptions omit these failures.

**Required outcome:** deletion/recovery must also govern pending REST snapshots and
buffer drainage. Correctness cannot require every cleared frame to survive a deliberately
lossy feed. Cover delivered and dropped clears across initial fetch/refetch settlement,
while preserving legitimate post-clear completions and reused IDs. Extend the approved
in-band design to satisfy these outcomes; this report does not select a schema or
protocol change without approval.

### 2.3 R26 — P2: Aborting usage-injection body preparation bypasses lifecycle cleanup

**Newly identified related path; not a recurrence of stopped-admission R25.**

**Location:** [ProxyHandler.cs:95](E:/Code/Vessel/src/Vessel/Proxy/ProxyHandler.cs:95).

The handler registers Started, then awaits PrepareRequestBody **before entering** the
try/finally that enqueues the record or completes a rejected capture. When
injectStreamUsage is enabled, preparation reads the request body. A client disconnect
during that read throws before the finalizer is installed. No captured record or terminal
event is produced, and the sequence remains in the authoritative active set.

**Real HTTP reproduction:**

1. Start the app with a temporary config/database and an OpenAI-type local stub backend
   with injectStreamUsage=true.
2. Send POST /v1/chat/completions with Content-Length 4096 but only a JSON prefix.
3. Wait until the request appears active, then close the TCP connection.
4. Send a normal control request and inspect active/history state after settlement.

The interrupted request **seq 33 remained active**. The control request returned HTTP
200 and advanced the completed watermark to 34; history contained only that control
request, not the aborted upload. Because the registry still lists 33, normal reconciliation
also preserves the false running entry. Repeated cancellations can accumulate entries
even while capture is healthy and no SSE client is connected.

The await placement predates round three; it was exposed while checking Batch H's
registered-to-terminal guarantee. R25's reported stop/admission/drain cases are fixed,
but that guarantee does not yet cover every path after registration.

**Required outcome:** cover request preparation and its failures with lifecycle
finalization, preserving the capture/error policy for interrupted requests. Add an
aborted usage-injection upload to the existing integration tests and assert both terminal
registry state and the intended captured/error result. Forwarding must remain independent
of capture health.

## 3. Contract and documentation assessment

The [approved H0 decisions](E:/Code/Vessel/docs/code-review-phase-4-plan.md:960) are treated
as the current design contract. The in-band clear, server-run identity, shared snapshot
lock and admission result are implemented. The failures above show missing interactions
within those mechanisms, not a need to replace YARP, SQLite, the writer or the React UI.

| Decision | Assessment |
| --- | --- |
| D01 — Stored wire bytes / bounded decoding | Remains resolved; decode notices and raw selection now work together. |
| D02 — Unsupported non-streamed tok/s | Remains resolved; no unsupported duration-based estimate reintroduced. |
| D03 — Host/browser-origin guard | Tests pass. This remains a local control-plane guard, not authentication or a public-hosting guarantee. |
| D04 — Documentation/acceptance accuracy | **Still partial.** Specific contradictions and overclaims below need correction in their owning text. |
| D05 — Session-only in-flight filtering | Preserved as approved; accuracy still depends on lifecycle correctness. |
| R14b — Cross-tab SQLite identity reuse | Accepted caveat remains; R23's same-tab failures are separate. |

D04 has three concrete remaining problems:

- [phase-3.md:99](E:/Code/Vessel/docs/phase-3.md:99) still describes boundaryId and the
  retired client-generation behavior as the API contract, despite the later section
  saying that field is gone. Replace the stale contract in place.
- The [round-three closing table](E:/Code/Vessel/docs/code-review-phase-4-plan.md:1090)
  says R11/R23 and the terminal guarantee are fully demonstrated. That is too broad
  given the reproduced failures above.
- The [live-burst account](E:/Code/Vessel/docs/code-review-phase-4-plan.md:1062) says the
  tab was never reloaded, while [its caveat](E:/Code/Vessel/docs/code-review-phase-4-plan.md:1080)
  records forced re-navigation. Those are different verification conditions; do not
  count recovery by navigation as proof of an uninterrupted same-tab gate. The current
  review also observed a crash state and did not establish its cause.

Future replay/diff/copy-as-curl, pricing, Ollama panels, release CI and the multi-platform
release matrix are not Phase 4 findings. No adjacent implementation or product/design
decision was made during this review.

## 4. Test coverage and engineering assessment

The new tests use the right components for the originally reproduced defects: a real
VesselApp for stopped admission, the hub for snapshot consistency, and DetailPane for the
raw toggle. Server hello is now tested against status and active identities. These
controls pass independently as well as in the full suite.

The remaining gaps are specific:

- Snapshot coherence is not snapshot freshness. Test a response arriving after a new
  hello or a newly published started event.
- SSE order does not order REST snapshots. Test stale initial results and a dropped
  clear alongside completion-buffer drainage.
- A finalizer only covers code inside its try. Test cancellation at the preparation
  await, not only successful forwarding after admission stops.

Prefer extending these existing suites. The external reproductions use the production
hook/components, real QueryClient, production backend classes and synthetic local HTTP
traffic. No failing assertion was skipped or changed to accept an incorrect result.

Lint completes with **six existing warnings**. The production bundle is **502.70 kB
minified**, above Vite's 500 kB warning threshold. These are maintainability observations,
not additional demonstrated correctness findings; the build is not warning-free.

## 5. Verification actually performed

### Automated and targeted checks

| Check | Result |
| --- | --- |
| dotnet test --solution Vessel.sln | **269 passed, 0 failed, 0 skipped** in each of three consecutive runs: 16.067, 15.723 and 15.687 seconds. |
| npm test | **62 passed across nine files**, in both executions this round. |
| Production TypeScript/Vite build | Passed as part of the clean publish; 502.70 kB chunk warning. |
| npm run lint | Completed with six warnings. |
| External frontend probes | **3 passed, 4 failed** in each of two runs. Controls: original restart, original raw-stream selection, normal decode warning. Failures: two R11 and two R23 orderings. |
| Prior concurrent-snapshot probe | **606 snapshots, zero inconsistent** after the shared-lock change. |
| Stopped-admission control | **32/32 HTTP 200**, zero active entries; repository tests additionally cover subscribers/no subscribers and writer give-up. |
| Interrupted upload | Seq 33 remained active after disconnect; normal seq 34 completed/persisted. R26 confirmed through real HTTP. |
| Late registration probe | Production contexts/hub permit an earlier allocated seq to register after a snapshot whose completed boundary already exceeds it. |

### Clean publish

The unchanged current smoke script passed from a **327-file clean source copy** with no
pre-existing dist/bin/obj/node_modules. The self-contained win-x64 executable was **102.9
MB**. Resource presence, status/backend listing, intact proxy path/body, unknown-backend
404, SPA and hashed JS asset checks passed.

An external script copy preserved every current assertion; source-root selection and
cleanup were adjusted only to retain evidence. It used an ephemeral port and temporary
config/database.

The script still performs one launch with prewritten configuration. This success does
not independently verify first-run config creation or same-database restart, and does
not root-cause the SQLite restart error observed in round one. The config-creation unit
test remains passing.

### Browser and live burst

Browser work used the clean source's real VesselApp with its embedded production UI and
an isolated stub/config/database, separate from the executable smoke.

Before the burst, Response → Raw stream displayed the actual three captured NDJSON
lines for an unknown-format response. This independently confirms R24's UI fix.

The burst sent **10,000 streamed requests at concurrency 24 across 100 tags**, with the
stub pausing 20 ms between its three NDJSON chunks. Submission took **25.958 seconds**.
The store retained **10,000 rows**, reported **zero failures**, and returned **100 tag
facets**. The active endpoint was empty with completed boundary 10001 afterwards.
This is a synthetic stress exercise, not a production benchmark.

The live tab was open before submission. During the burst, inspection returned
“playwright.evaluate exceeded its deadline”; the next attempt returned “Inspected target
navigated or closed.” Browser tab discovery then reported the original tab titled
**“This page crashed”** with an internal crash-page URL. Browser URL policy blocked direct
inspection of that internal page; no workaround was attempted. No crash dump or
root-cause trace was obtained.

A **fresh tab** successfully loaded persisted history. At 1280×720, the tag region stayed
84 px and history retained a **409 px viewport**, both collapsed and expanded. Expanding
revealed 100 tags; selecting tag 050 showed matching rows. This verifies post-burst layout,
not uninterrupted live recovery of the original tab.

The literal under-ten-second search litmus and a fresh real-model multi-turn/tool session
were not repeated. Fixture tests do not substitute for those manual gates. The review
host/stub were stopped after testing; the user's default-port instance, configuration and
database were not modified.

## 6. Evidence retained locally

All additional probes and logs are outside the repository:

- Backend suites: [run 1](C:/Users/spenc/AppData/Local/Temp/vessel-phase4-round3-review/backend-tests-1.log),
  [run 2](C:/Users/spenc/AppData/Local/Temp/vessel-phase4-round3-review/backend-tests-2.log),
  [run 3](C:/Users/spenc/AppData/Local/Temp/vessel-phase4-round3-review/backend-tests-3.log).
- [Frontend suite](C:/Users/spenc/AppData/Local/Temp/vessel-phase4-round3-review/frontend-tests.log)
  and [lint](C:/Users/spenc/AppData/Local/Temp/vessel-phase4-round3-review/frontend-lint.log).
- [External frontend failures](C:/Users/spenc/AppData/Local/Temp/vessel-phase4-round3-review/frontend-probes.log)
  and [repeat](C:/Users/spenc/AppData/Local/Temp/vessel-phase4-round3-review/frontend-probes-repeat.log),
  with [hook probe source](C:/Users/spenc/AppData/Local/Temp/vessel-clean-9a0bb94d1a484d6a8dcb6afd33345824/frontend/src/api/round3-review.test.ts)
  and [DetailPane probe source](C:/Users/spenc/AppData/Local/Temp/vessel-clean-9a0bb94d1a484d6a8dcb6afd33345824/frontend/src/components/round3-detail-review.test.ts).
- [C# probes](C:/Users/spenc/AppData/Local/Temp/vessel-phase4-round3-review/probes/Program.cs)
  and [results](C:/Users/spenc/AppData/Local/Temp/vessel-phase4-round3-review/backend-probes.log).
- [Publish/build log](C:/Users/spenc/AppData/Local/Temp/vessel-phase4-round3-review/publish.log),
  [10k output/timings](C:/Users/spenc/AppData/Local/Temp/vessel-phase4-round3-review/seed.log),
  [browser observation record](C:/Users/spenc/AppData/Local/Temp/vessel-phase4-round3-review/browser-observations.md).

These are temporary review artifacts, not project dependencies. Section 2 provides
reproducible sequences suitable for the existing repository suites.

## 7. Conditions for closing this review

1. Complete R11's response-freshness handling without deleting legitimate live entries.
2. Complete R23 across pending REST results, dropped clear events and buffer settlement,
   while retaining the now-passing survivor/ID-reuse cases.
3. Fix R26 so cancellation during preparation cannot bypass lifecycle finalization.
4. Keep all current tests passing and add these failing interaction cases to existing
   suites. Retain the passing round-three controls.
5. Investigate the observed live-tab crash and demonstrate the 10k/100-tag gate without
   a reload or replacement tab; record any remaining environment limitation precisely.
6. Correct stale API descriptions and acceptance claims in place. Keep clean-publish
   success distinct from unverified first-run/restart behavior.

No application fixes or new design choices are implemented by this report.
