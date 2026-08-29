# Code review through Phase 4 — round-six verification

Date: 29 August 2026 (Europe/London)

Reviewed revision: `1234571` — `Code review feedback round 6`

Comparison: `1eac28b..1234571`, plus the current implementation, tests and approved
Batch K decisions. The working tree was clean before review.

Scope: verify the remaining R11/R23 fixes against the brief, architecture, Phase 0–4
specifications, plan and engineering practice; sweep the changed contracts and call sites;
repeat the complete suites, clean publish and visible same-tab live gate; retain earlier
closures where the current change and regression evidence give no reason to reopen them.
This replaces the previous report's current assessment in place.

## Assessment

**The two requested findings are fixed. R11 and R23 are resolved.** Cancel-then-refetch
now makes the post-clear/recovery list read genuinely distinct, and the active snapshot's
descriptors reconstruct a request whose `started` frame was lost. The three independent
closure probes pass with the corrected timing, the new repository regressions cover them,
and the real clear/post-clear path passes in the same production tab.

**All prior findings R01–R26 are resolved.** The repository is not completely clear,
however: the wider pass found two smaller P3 issues outside the requested closure cases:

- **R27:** if a `first_token` frame is dropped, recovery restores the active row but cannot
  restore its TTFT because the server snapshot deliberately omits that field.
- **R28:** `RetentionTests.MaxDbSize_FileShrinksUnderCap` can combine a pre-retention row
  list with a post-retention file-size reading. It failed one complete run despite the
  production retention having completed correctly.

These are lower severity than the resolved R11/R23 failures: R27 loses a live metric, not
the request row, and R28 is a test-observation race rather than a product retention failure.
They still prevent an unqualified clear review.

Verification summary:

- Backend: first complete run **272/273** because R28 manifested; the next three complete
  runs passed **273/273**, zero skips. The isolated retention test passed 10/10.
- Frontend: **79/79 passed in three consecutive complete runs**.
- Focused external probes: the three R11/R23 closure cases passed; the R27 probe failed
  with `ttftMs === undefined`, identically on repeat.
- Clean self-contained publish, embedded-resource and proxy smoke: passed.
- Visible live gate: 10,000-request burst, clear to zero and 100 post-clear requests passed
  in one tab; browser warning/error log empty.
- Lint: exit 0 with six existing warnings; production build succeeded with the existing
  bundle-size warning.

Only this report was changed in the repository. No implementation/test changes or commits
were made, and no failing test was skipped, weakened or deleted. Probe sources, logs and
publish artifacts are retained outside the repository.

## 1. Closure status

### Findings verified in Batch K

| ID | Status | Current evidence |
| --- | --- | --- |
| **R11 — Lifecycle recovery** | **Resolved** | `/active` now returns locked, ordered descriptors with the `started` metadata and learned model. Recovery rebuilds from those descriptors rather than intersecting server-active IDs with locally known starts. The lost-`started` probe displays the real path, method, backend, tags, start time and model; randomized tests now assert two-sided active-set equality. R27 is the narrower, separately tracked live-TTFT omission. |
| **R23 — Clear ordering/recovery** | **Resolved** | The list query passes TanStack's abort signal to `fetch`; recovery/clear cancels outstanding request queries before refetching. Both held-initial-response probes now start a second fetch before the old response is released, and the cancelled result cannot restore the deleted row. No ID/timestamp client predicate was reintroduced. |
| **D04 — Documentation accuracy for R11/R23** | **Resolved** | Architecture, Phase 3 and the remediation plan describe cancel-then-refetch, descriptor recovery and the demonstrated limitations accurately. |

### New findings from this pass

| ID | Priority | Status | Summary |
| --- | --- | --- | --- |
| **R27 — Lost first-token TTFT is unrecoverable** | P3 | Open | The active descriptor carries the learned model but not TTFT. A dropped `first_token` therefore leaves a recovered long-running row without its live TTFT; §2.1. |
| **R28 — Retention integration test observes two database states** | P3 | Open | The readiness predicate reads rows and file size separately and can return a stale ten-row list after retention has already shrunk the database; §2.2. |

### Earlier findings

These closures are carried forward after inspection of the Batch K diff and current
complete suites. Earlier manual security/configuration exercises were not all repeated.

| ID | Status | Qualification |
| --- | --- | --- |
| R01 — Clean publish omits UI | Resolved | Clean publish/resource/SPA/asset/proxy smoke passed again. |
| R02 — Stale config routing | Resolved | Request-scoped routing remains; concurrency tests pass. |
| R03 — Captured Markdown makes requests | Resolved | Inert captured content, image mediation and CSP remain covered. |
| R04 — Settings focus loss | Resolved | Stable dialog lifecycle and scoped clocks remain; earlier interactive evidence is carried forward. |
| R05 — Decode allocation / invisible truncation | Resolved | Bounded decoding and body-local notices remain covered. |
| R06 — Queue accepts after writer stops | Resolved | Closed admission and command failure behavior remain covered. |
| R07 — Clear overtakes queued captures | Resolved | FIFO writer/clear ordering passes. |
| R08 — Partial response enrichment lost | Resolved | Interruption/provenance integration tests pass. |
| R09 — Ollama thinking/tools lost | Resolved | Adapter, golden and renderer tests pass. |
| R10 — Initial fetch loses completions | Resolved | Completion buffering remains effective alongside the new query cancellation. |
| R12 — Tags hide list | Resolved | The live tab remained usable with 100 tags. |
| R13 — Backend rename collision | Resolved | Guard unchanged; previous interactive verification is carried forward. |
| R14 — Clear leaves stale selected detail | Resolved for the reported same-tab case | Ack-driven detail eviction remains. Cross-tab ID reuse remains an accepted qualification. |
| R15 — Null config causes 500 | Resolved | Validation/preservation tests pass. |
| R16 — Restart warning disappears | Resolved | Bound-listener/repeated-save tests pass. |
| R17 — Malformed render blanks app | Resolved | Validation, local boundaries and raw fallback pass. |
| R18 — Image preview missing | Resolved | Extraction and captured-image preview tests pass. |
| R19 — SSE EOF counted as blank line | Resolved | Parser/adapter terminal and incomplete-stream cases pass. |
| R20 — Intermittent stats rounding assertion | Resolved for that assertion | Deterministic stats fixtures remain. R28 is a different integration-test race. |
| R21 — Failed save destroys config | Resolved | Atomic replacement/preservation tests pass; no power-loss durability claim. |
| R22 — SSE publication IDs reorder | Resolved | Ordered publish/fan-out and gap controls pass. |
| R24 — Raw stream unavailable in fallback | Resolved | DetailPane regressions pass. |
| R25 — Active registry grows after capture stops | Resolved for the reported admission/drain paths | Writer give-up, stop-drain and terminal controls pass with descriptors. |
| R26 — Upload abort bypasses finalization | Resolved | Terminal lifecycle/error capture controls pass; no partial-body-salvage claim. |

## 2. Remaining findings

### 2.1 R27 — P3: a dropped `first_token` cannot be recovered

Locations: [server publishes but does not retain TTFT](E:/Code/Vessel/src/Vessel/Capture/CaptureEvents.cs:218),
[active descriptor shape](E:/Code/Vessel/src/Vessel/Capture/CaptureEvents.cs:333),
[client can carry TTFT only when already known](E:/Code/Vessel/frontend/src/api/useLiveHistory.ts:541),
[the current known-TTFT-only regression](E:/Code/Vessel/frontend/src/api/useLiveHistory.test.ts:1020),
[documented limitation](E:/Code/Vessel/docs/phase-3.md:290).

K0b correctly retains the `started` fields and model in `_active`. `FirstToken`, unlike
`RequestReady`, only publishes the event; it does not update the descriptor. During recovery,
`toInFlight` can preserve `known.ttftMs`, but it has no source for a value the client never
received.

Controlled sequence:

1. `started(seq=2)`, ID 1, reaches the client.
2. `first_token(seq=2, ttftMs=42)`, ID 2, is dropped by the supported bounded queue.
3. An unrelated `started(seq=3)`, ID 3, exposes the gap while request 2 is still running.
4. Recovery returns both active descriptors at log position 3.

**Actual:** request 2 is correctly displayed, but `ttftMs` is `undefined`.
**Expected:** its already-measured live TTFT, 42 ms.

The result is identical on repeat. The existing regression proves that a TTFT the client
already received survives a snapshot rebuild; it does not cover loss of the `first_token`
frame itself. Phase 3 records the omission, but the live-row contract says `first_token`
shows live TTFT, and the loss/recovery mechanism is meant to recover dropped lifecycle state.

**Required correction:** retain optional TTFT in the same locked active descriptor when
`FirstToken` fires, return it from `/active`, and rebuild it authoritatively. Add the dropped
frame sequence above alongside the existing known-TTFT test. If omitting live TTFT after
loss is instead an intended product trade, that narrower guarantee needs explicit approval;
this review does not infer it merely from the implementation note.

### 2.2 R28 — P3: the max-size retention test mixes stale rows with a fresh size

Locations: [the two-read readiness predicate](E:/Code/Vessel/tests/Vessel.Tests/RetentionTests.cs:60),
[the polling helper returns the earlier projection](E:/Code/Vessel/tests/Vessel.Tests/CaptureDb.cs:164).

The first complete backend run failed:

```text
RetentionTests.MaxDbSize_FileShrinksUnderCap
expected deletions, all 10 rows still present
```

The production writer had not failed retention. The test's predicate receives a row list
from `CaptureDb.Query`, then opens a separate connection in `DatabaseSizeBytes`. Retention
can delete/vacuum between those reads:

1. `Query` returns all ten pre-retention rows.
2. The writer enforces the size cap and vacuums.
3. `DatabaseSizeBytes` observes the post-retention file at or below 1 MB.
4. `WaitUntil` accepts the predicate and returns the stale ten-row projection; the next
   assertion fails even though a fresh query would show the deletions.

This matches the observed failure exactly. The next three complete suites passed 273/273,
and the isolated test passed 10/10, which is expected for a narrow scheduling window and
does not make the predicate atomic.

**Required correction:** observe the rows and size from one stable database state, or re-query
the rows after the size condition becomes true before returning/asserting. Keep all retention
assertions; do not weaken or skip the test. This is outside Batch K's implementation diff,
but a real failing gate and therefore part of the repository review.

## 3. Brief, architecture, plan and engineering assessment

The implementation remains aligned with the project's core structure: YARP forwarding,
bounded capture, a background SQLite writer, adapter enrichment and an embedded React SPA.
No replacement of those components is indicated.

Batch K's two design choices are sound for the findings they target:

- **K0a / R23:** `RequestList` passes the per-fetch signal through `api.listRequests` to
  browser `fetch`. `refetchAuthoritative` awaits cancellation before refetching request
  queries, so TanStack cannot reuse the pending initial retryer. Stats/facets invalidation
  remains coupled to that authoritative read, and REST rows remain unfiltered.
- **K0b / R11:** the server registry stores small immutable active descriptors, updates the
  model under the same publish lock as `request_ready`, and snapshots ordered descriptors
  with the log position. The client reconstructs the server set wholesale and retains object
  identity for unchanged rows.

The whole-tree contract sweep found the endpoint, API types, client, hook, RequestList query,
backend tests and frontend fixtures updated consistently. Historical `activeSeqs` text remains
only in explicitly superseded remediation-plan sections. Architecture and Phase 3 describe
the current wire shape.

The remaining design seam is R27: the snapshot now reconstructs the row but not all live state
the server has already measured. Adding nullable TTFT to the descriptor follows the same model
update pattern and does not require a new recovery protocol. R28 is independent test hygiene.

Future replay, diff, copy-as-curl, cost estimates, Ollama panels and the release-platform
matrix remain outside Phase 4. No future-phase omission is treated as a defect here.

## 4. Verification performed

| Check | Result |
| --- | --- |
| `dotnet test --solution Vessel.sln` | Run 1: 272 passed / 1 failed / 0 skipped (R28). Runs 2–4: 273 passed / 0 failed / 0 skipped; durations 25.289 s, 24.515 s and 25.031 s. |
| Isolated `MaxDbSize_FileShrinksUnderCap` | 10/10 passed; the observed full-suite failure remains explained by the non-atomic predicate. |
| `npm test -- --run` | 79 passed ×3 across 9 files; durations 18.58 s, 17.05 s and 17.15 s. |
| `npm run lint` | Exit 0; six existing warnings. |
| Focused production-hook probes | R23 received-clear stale response: passed; R23 recovered-clear stale response: passed; R11 lost-start descriptor reconstruction: passed; R27 dropped-first-token TTFT: failed ×2 (`undefined`, expected 42). |
| Clean publish smoke | Passed from a fresh 327-file source copy containing no initial `dist`, `bin`, `obj` or `node_modules`: frontend build, self-contained single-file win-x64 executable, embedded resources, status, proxy path/body, unknown-backend 404, SPA and hashed JS asset. |
| Visible live browser gate | Passed: live active descriptor, 10,000-request burst, clear to zero, then 100 post-clear requests in the same production tab. |

The published executable was approximately **102.9 MB**. The minified JS bundle was
**504.40 kB** and triggered Vite's 500 kB warning. Build success is not a warning-free
build. `npm ci` reported 245 packages and zero vulnerabilities in that install; this is
not a broader supply-chain or release-security audit.

The clean smoke used a prewritten isolated config, temporary database and ephemeral port.
It does not independently verify first-run default-config creation, restart persistence,
live-key remote providers or the multi-platform release matrix.

## 5. Live browser gate

A separate current VesselApp host served production assets built from the clean source,
using a temporary SQLite database and local streaming stub. No Vite development server was
used and the user's normal Vessel instance/data were untouched.

An initial long-running request appeared in `/active` as a descriptor containing its seq,
start time, session, method, path, backend and tags. Its earliest snapshot had `model: null`,
before the asynchronous `request_ready` update; that is a valid intermediate descriptor.

The main workload sent **10,000 requests at concurrency 24 across 100 tags**, with 20 ms
streamed-stub delays. Submission completed in **25.99 seconds**. After settlement:

- The same visible tab and API showed **10,000 requests, 0 failures** and no live pulse.
- `/active` returned `active: []` at log position 40,004.
- Clear-all deleted all 10,000 retained rows; the tab converged to zero, empty facets and
  the empty-state message.
- A further 100 requests exercised post-clear SQLite ID reuse. Three seconds later the tab
  showed **100 requests, 0 failures**, ten post-clear tags and no live pulse; `/active`
  returned an empty descriptor array at log position 40,405.
- The browser warning/error log was empty. The tab stayed connected and never crashed,
  reloaded, navigated or was replaced.

Seven-sample local medians during the 10k dataset were 2.05 ms for the list, 28.22 ms for
FTS, 17.92 ms for a tag filter, 48.10 ms for facets and 13.41 ms for stats. These are
synthetic local observations, not production benchmarks. A model-filter sample used the
wrong model name and returned zero rows, so it is excluded.

The live gate does not deliberately drop a frame or hold an initial list response across a
clear; the focused probes exercise those interleavings. The isolated host was stopped and
the browser tab closed after verification.

## 6. Retained evidence

[Evidence index](C:/Users/spenc/AppData/Local/Temp/vessel-phase4-round6-review/README.md)

[Focused probe source](C:/Users/spenc/AppData/Local/Temp/vessel-round6-clean-1234571/frontend/src/api/round6-review.test.ts),
[probe run 1](C:/Users/spenc/AppData/Local/Temp/vessel-phase4-round6-review/frontend-probes-3.log),
[identical repeat](C:/Users/spenc/AppData/Local/Temp/vessel-phase4-round6-review/frontend-probes-4.log)

[Backend failing run](C:/Users/spenc/AppData/Local/Temp/vessel-phase4-round6-review/backend-tests-1.log),
[green run 1](C:/Users/spenc/AppData/Local/Temp/vessel-phase4-round6-review/backend-tests-2.log),
[green run 2](C:/Users/spenc/AppData/Local/Temp/vessel-phase4-round6-review/backend-tests-3.log),
[green run 3](C:/Users/spenc/AppData/Local/Temp/vessel-phase4-round6-review/backend-tests-4.log),
[isolated retention runs](C:/Users/spenc/AppData/Local/Temp/vessel-phase4-round6-review/retention-isolated.log)

[Frontend run 1](C:/Users/spenc/AppData/Local/Temp/vessel-phase4-round6-review/frontend-tests-1.log),
[run 2](C:/Users/spenc/AppData/Local/Temp/vessel-phase4-round6-review/frontend-tests-2.log),
[run 3](C:/Users/spenc/AppData/Local/Temp/vessel-phase4-round6-review/frontend-tests-3.log),
[lint log](C:/Users/spenc/AppData/Local/Temp/vessel-phase4-round6-review/frontend-lint.log)

[Clean publish log](C:/Users/spenc/AppData/Local/Temp/vessel-phase4-round6-review/publish.log),
[active-descriptor log](C:/Users/spenc/AppData/Local/Temp/vessel-phase4-round6-review/active-descriptor.log),
[10k workload log](C:/Users/spenc/AppData/Local/Temp/vessel-phase4-round6-review/seed.log),
[post-clear workload log](C:/Users/spenc/AppData/Local/Temp/vessel-phase4-round6-review/post-clear.log),
[browser observations](C:/Users/spenc/AppData/Local/Temp/vessel-phase4-round6-review/browser-observations.md)
