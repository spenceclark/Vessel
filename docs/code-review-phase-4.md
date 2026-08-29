# Code review through Phase 4 — round-seven verification

Date: 29 August 2026 (Europe/London)

Reviewed revision: `3b9ffb1` — `Code review feedback round 7`

Comparison: `1234571..3b9ffb1`, followed by a whole-tree contract sweep and clean-copy
verification. The working tree was clean before review.

Scope: re-review R27 and R28 against the brief, architecture, Phase 0–4 specifications,
plan and engineering practice; check every changed contract and call site; repeat the full
suites, focused failure sequences, clean publish and visible production-browser gate; and
retain earlier closures where the current tree gives no reason to reopen them. Future phases
remain out of scope.

## Assessment

**Clear through Phase 4. R27 and R28 are resolved, all findings R01–R28 are closed, and
this pass found no current P1, P2 or P3 issue.**

R27 now retains live TTFT in the server-authoritative active descriptor under the same lock
as event publication. A recovery snapshot therefore contains the measured TTFT even when the
subscriber never received `first_token`. The client rebuilds TTFT from that descriptor rather
than from whatever it happened to know before the gap. The exact dropped-frame sequence that
previously returned `undefined` now returns 42 ms in both repository and independent probes.

R28 now reads retention rows and database page size through one connection and one read
transaction. The readiness predicate cannot accept a post-retention size beside a stale
pre-retention row list. The formerly intermittent integration test passed 20/20 isolated runs
and every full backend run.

Verification summary:

- Backend: **273/273 passed in five complete runs**, zero failures or skips; the R28 test
  additionally passed **20/20** isolated runs.
- Frontend: **80/80 passed in three consecutive complete runs** across nine files.
- Focused external recovery probes: **4/4 passed twice**, including a completely dropped
  `first_token` frame.
- Clean self-contained publish, embedded-resource and proxy smoke: passed.
- Visible live gate: mid-flight TTFT, a 10,000-request burst, clear to zero and 100 post-clear
  requests passed in one production tab; browser warning/error log empty.
- Lint: exit 0 with six existing warnings. Production build passed with the existing bundle
  size warning.

Only this report was changed by the reviewer. No implementation or test was changed, no test
was skipped or weakened, and no commit was made. Evidence and probe sources are retained
outside the repository.

## 1. Requested finding closure

### R27 — Resolved: dropped `first_token` TTFT is recoverable

Locations: [server lifecycle hub](E:/Code/Vessel/src/Vessel/Capture/CaptureEvents.cs),
[wire type](E:/Code/Vessel/frontend/src/api/types.ts),
[client reconciliation](E:/Code/Vessel/frontend/src/api/useLiveHistory.ts),
[server regressions](E:/Code/Vessel/tests/Vessel.Tests/EventsTests.cs),
[client regressions](E:/Code/Vessel/frontend/src/api/useLiveHistory.test.ts),
[Phase 3 contract](E:/Code/Vessel/docs/phase-3.md).

The active registry now stores nullable `TtftMs`. `FirstToken` always updates the active
descriptor, including when there are no SSE subscribers, and does so while holding
`_publishLock`. When there are subscribers, the descriptor update and `first_token` publish
therefore occupy the same critical section as the snapshot's `logPosition`:

- a snapshot before the event is followed by replay of the event above its position;
- a snapshot at or after the event already contains the TTFT;
- a subscriber that dropped the event still gets the TTFT from the descriptor.

`toInFlight` treats the snapshot value as authoritative. The old known-row fallback was
removed, avoiding a second source of lifecycle truth. Model and TTFT updates preserve one
another because both replace the immutable descriptor with a `with` update under the same
lock.

Coverage now pins the important boundaries:

- an active descriptor starts with `model: null` and `ttftMs: null`;
- both fields update without a subscriber;
- the endpoint emits populated and null TTFT values in the camel-case wire shape;
- terminal completion still removes the descriptor;
- a client that receives `started` ID 1, loses `first_token` ID 2 and detects the gap at ID 3
  rebuilds request 2 with `ttftMs: 42` from `/active` alone.

The independent production-hook version of that sequence passed twice. A real streaming
request also appeared in the production UI while active with `TTFT 354ms`.

### R28 — Resolved: retention readiness observes one database state

Locations: [stable query helper](E:/Code/Vessel/tests/Vessel.Tests/CaptureDb.cs),
[retention integration test](E:/Code/Vessel/tests/Vessel.Tests/RetentionTests.cs).

`QueryWithSize` opens one read-only SQLite connection, begins one transaction, reads the rows,
then reads `page_count * page_size` through that same connection and transaction. The polling
helper returns that tuple, and the test asserts the returned rows rather than issuing a later
projection.

This removes the previous two-connection interleaving without weakening any assertion. The
test still requires the newest request, size at or below 1 MB, deletion of older rows and
survival of the newest row. Whole-tree search found no stale call site; the existing generic
`WaitUntil` users remain unchanged.

## 2. Finding register

| Finding | Status | Current verification |
| --- | --- | --- |
| R01 — Clean publish omits UI | Resolved | Fresh 327-file source copy contained no initial build output; publish embedded and served the SPA and hashed asset. |
| R02 — Stale config routing | Resolved | Request-scoped routing remains; concurrency/config suites pass. |
| R03 — Captured Markdown makes requests | Resolved | Inert rendering, image mediation and CSP remain covered. |
| R04 — Settings focus loss | Resolved | Stable dialog lifecycle remains; prior interactive closure carried forward. |
| R05 — Decode allocation / invisible truncation | Resolved | Bounded decoding and body-local notices pass. |
| R06 — Queue accepts after writer stops | Resolved | Closed admission and command failure paths pass. |
| R07 — Clear overtakes queued captures | Resolved | FIFO writer/clear ordering passes. |
| R08 — Partial response enrichment lost | Resolved | Interruption/provenance integration tests pass. |
| R09 — Ollama thinking/tools lost | Resolved | Adapter, golden and renderer tests pass. |
| R10 — Initial fetch loses completions | Resolved | Completion buffering and query interaction probes pass. |
| R11 — Lifecycle recovery | Resolved | Full active descriptors reconstruct lost starts; R27 closes the remaining TTFT field. |
| R12 — Tags hide list | Resolved | The visible tab remained usable with 100 tags at 10,000 rows. |
| R13 — Backend rename collision | Resolved | Guard unchanged; prior interactive closure carried forward. |
| R14 — Clear leaves stale detail | Resolved for the reported same-tab case | Clear and post-clear ID reuse passed in one tab. Cross-tab ID reuse remains the documented qualification. |
| R15 — Null config causes 500 | Resolved | Validation/preservation tests pass. |
| R16 — Restart warning disappears | Resolved | Bound-listener/repeated-save tests pass. |
| R17 — Malformed render blanks app | Resolved | Validation, local boundaries and raw fallback pass. |
| R18 — Image preview missing | Resolved | Extraction and captured-image preview tests pass. |
| R19 — SSE EOF counted as blank line | Resolved | Parser/adapter terminal and incomplete-stream matrices pass. |
| R20 — Intermittent stats rounding assertion | Resolved | Deterministic fixtures pass; R28's separate race is now closed. |
| R21 — Failed save destroys config | Resolved | Atomic replacement/preservation tests pass; no power-loss durability claim. |
| R22 — SSE publication IDs reorder | Resolved | Ordered publication, fan-out and gap controls pass. |
| R23 — Clear ordering/recovery | Resolved | Cancel-then-refetch and both held-response probes pass. |
| R24 — Raw stream unavailable in fallback | Resolved | DetailPane regressions pass. |
| R25 — Active registry grows after capture stops | Resolved | Give-up, stop-drain and terminal controls pass with enriched descriptors. |
| R26 — Upload abort bypasses finalization | Resolved | Terminal lifecycle/error capture controls pass; no partial-body salvage claim. |
| R27 — Lost first-token TTFT | Resolved | Locked descriptor retention, wire contract and dropped-frame recovery pass. |
| R28 — Retention test observes two states | Resolved | One-transaction observation passes all full runs and 20/20 focused runs. |

## 3. Brief, architecture, plan and engineering assessment

The implementation remains consistent with the project's intended structure and Phase 4
boundary:

- YARP remains the transparent forwarding path; control headers are stripped and traffic is
  not buffered for capture.
- Capture remains bounded and asynchronous, with SQLite writes owned by the writer service and
  read-side endpoints separated from forwarding.
- Format detection/enrichment remains adapter based, with wire-true body storage, bounded
  decoding and raw fallback.
- The React SPA remains embedded in the executable and obtains history through REST plus an
  ordered, lossy SSE feed recovered by a coherent server snapshot.
- Sessions, filters, stats, request detail, settings, retention and the virtualized live list
  satisfy the brief and completed Phase 0–4 plan items exercised here.

R27 follows the architecture rather than adding a parallel recovery mechanism: the active
descriptor is the server's existing lifecycle authority, and the same lock relates it to the
event log. R28 changes only how the integration test observes SQLite; production retention
logic and its assertions remain intact.

The change respects the house style and keeps private fields underscore-prefixed. Contract
changes were propagated through server records, JSON, TypeScript, reconciliation logic,
fixtures, tests and Phase 3 documentation. Historical `activeSeqs`/pre-Batch-L language is
confined to explicitly superseded remediation history. `git diff --check` is clean.

Future replay, diff, copy-as-curl, cost estimates, provider-specific panels and the release
platform matrix are later-phase work and are not findings in this review.

## 4. Verification performed

| Check | Result |
| --- | --- |
| Backend xUnit executable | 273/273 passed in five complete runs, zero failures/skips. The fifth retained summary took 20.895 s. |
| `MaxDbSize_FileShrinksUnderCap` | 20/20 isolated runs passed. |
| `npm test -- --reporter=dot` | 80/80 passed ×3 across nine files; 35.51 s cold, then 16.37 s and 16.41 s. |
| `npm run lint` | Exit 0; six existing warnings. |
| Focused external production-hook probes | 4/4 passed ×2: both R23 held-response cases, lost-`started` reconstruction and completely lost-`first_token` TTFT recovery. |
| Clean publish smoke | Passed from a source copy with no initial `dist`, `node_modules`, `bin` or `obj`; frontend build, single-file win-x64 artifact, embedded resources, status, proxy, error path, SPA and asset all passed. |
| Visible production-browser gate | Passed: live TTFT, 10,000 requests, clear to zero, then 100 post-clear requests in the same tab; browser warning/error log empty. |
| Static gates | Whole-tree call-site/contract sweep and `git diff --check` passed. |

### Test-runner environment caveat

On this host, .NET SDK 10.0.301's Microsoft.Testing.Platform path for `dotnet test` returned
exit code 5 with `Zero tests ran`. The standalone xUnit executable discovered and ran all 273
tests. A fresh project generated from the official xUnit v3 4.0.0 template reproduced the
same `dotnet test` result under the same installed SDK, so this review treats it as a local
SDK/runner discrepancy rather than a Vessel finding. The repository's `global.json` matches
the [official .NET 10 MTP configuration](https://xunit.net/docs/getting-started/v3/microsoft-testing-platform).
The diagnostic log and minimal template reproduction are retained with the evidence; no zero-test
run was counted as a pass.

The published executable was approximately **103 MB**. The minified JS bundle was
**504.40 kB** and triggered Vite's existing 500 kB warning. `npm ci` reported 245 packages and
zero known vulnerabilities in that install. These facts do not constitute a release-size,
supply-chain or multi-platform audit.

## 5. Live browser gate

A separate current VesselApp host served the production assets built from the clean source,
using a temporary SQLite database, an ephemeral port and the local streaming stub. No Vite
development server or normal Vessel data was used.

- A long-running stream appeared while active with `TTFT 354ms`; all four chunks subsequently
  completed.
- The workload sent **10,000 requests at concurrency 24 across 100 tags** in 25.862 seconds.
  The same tab and API settled at **10,000 requests, 0 failures**, 100 tags and no active
  request; `/active` reported log position 40,003.
- Clear-all converged the tab to zero requests, zero facets and the empty state.
- A further **100 requests at concurrency 12 across 100 post-clear tags** completed in
  0.588 seconds. The same tab settled at **100 requests, 0 failures** and no active request;
  `/active` reported log position 40,404.
- Browser warning/error logs were empty. The tab stayed connected and was not replaced,
  reloaded or navigated during the workload.

The focused probes, rather than this workload, deliberately exercise dropped frames and held
REST responses. The isolated host was stopped and the browser tab closed afterward.

## 6. Qualifications

The clean smoke used a prewritten isolated config, temporary database and ephemeral port. This
pass did not independently repeat first-run default-config creation, restart persistence,
live-key remote provider calls, hostile-content manual exercises or the multi-platform release
matrix. Automated coverage and earlier verified closures for those areas are carried forward.

The six lint warnings and the 500 kB bundle warning remain visible engineering debt, but this
review found no demonstrated Phase 4 correctness, security or acceptance failure behind them.

## 7. Retained evidence

[Evidence index](C:/Users/spenc/AppData/Local/Temp/vessel-phase4-round7-review-3b9ffb1/README.md)

[Focused probe source](C:/Users/spenc/AppData/Local/Temp/vessel-phase4-round7-review-3b9ffb1/clean/frontend/src/api/round7-review.test.ts),
[backend 273/273 summary](C:/Users/spenc/AppData/Local/Temp/vessel-phase4-round7-review-3b9ffb1/backend-summary.log),
[browser observations](C:/Users/spenc/AppData/Local/Temp/vessel-phase4-round7-review-3b9ffb1/browser-observations.md)

[MTP diagnostic](C:/Users/spenc/AppData/Local/Temp/vessel-phase4-round7-review-3b9ffb1/dotnet-diag),
[fresh-template reproduction](C:/Users/spenc/AppData/Local/Temp/vessel-phase4-round7-review-3b9ffb1/xunit-sample),
[publish smoke script](C:/Users/spenc/AppData/Local/Temp/vessel-phase4-round7-review-3b9ffb1/publish-smoke-retained.ps1),
[retained clean publish source](C:/Users/spenc/AppData/Local/Temp/vessel-clean-64a8932822ee4cbfb77d82d9647dd838),
[retained publish runtime](C:/Users/spenc/AppData/Local/Temp/vessel-smoke-7803be98168e45d6ba8ab074988fc96f)
