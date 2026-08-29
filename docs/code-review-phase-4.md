# Code review through Phase 4 — round-five verification

Date: 29 August 2026 (Europe/London)

Reviewed revision: `1eac28b` — `Code review feedback round 5`

Comparison: `99d0041..1eac28b`, plus the current implementation, tests and approved
Batch J contract. The working tree was clean before review.

Scope: re-review the latest R11/R23 remediation against the brief, architecture,
Phase 0–4 specifications, plan and engineering practice; repeat the complete suites,
clean publish and same-tab live gate; retain earlier closures where the current change
and regression evidence give no reason to reopen them. This replaces the previous
report's current assessment in place.

## Assessment

**Not yet clear.** Batch J is a substantial simplification and it fixes all four
interleavings reported in the previous round. Its server-side snapshot/log-position
contract is coherent. Two independently reproduced gaps remain:

- R23's “always a new fetch” rule is not true while an initial TanStack Query fetch is
  pending with no cached data. Both a received clear and a recovered missed clear can
  therefore finish with a deleted row restored by the old response.
- R11 recovery knows that a request is active but cannot render it if the lossy feed
  dropped its `started` frame, because `/active` carries only sequence numbers.

**24 of R01–R26 are resolved; R11 and R23 remain partial.** Both current findings are
P2 correctness issues. R11 now needs an explicit product/API decision; this review
does not choose one under the repository's no-guessing rule.

The ordinary live gate passes: the same visible production tab handled 10,000 streamed
requests, converged through clear-all to zero, then handled 100 post-clear requests
whose SQLite IDs were reused. It never reloaded, was not replaced and did not crash.

Verification summary:

- Backend: **271/271 passed in three consecutive complete runs**, zero skips.
- Frontend: **76/76 passed in three consecutive complete runs**.
- Focused external probes: **3/3 failed with the same final states on two runs**.
- Clean self-contained publish, embedded-resource and proxy smoke: passed.
- Lint: exit 0 with six existing warnings; production build: successful with the
  existing bundle-size warning.

Only this report was changed in the repository. No implementation or test was changed,
no test was skipped or weakened, and no commit was made. Probe sources, logs and publish
artifacts are retained outside the repository.

## 1. Closure status

### Findings under active verification

| ID | Status | Current evidence |
| --- | --- | --- |
| **R11 — Lifecycle recovery** | **Partial** | Batch J fixes the previously reported queued-start race: recovery removes queued frames at or below its snapshot position and replays later frames. Run identity, atomic registration and long-running-survivor controls also pass. A start that is lost before the client ever sees it leaves an active server request invisible until completion; §2.2. |
| **R23 — Clear ordering/recovery** | **Partial** | Batch J fixes the four round-four cases: queued pre-clear work, reused REST IDs and multiple missed clears now converge. A pending initial list fetch is still reused instead of superseded, so its pre-clear response can restore deleted history; §2.1. |
| **R26 — Upload abort bypasses finalization** | **Resolved** | The previous real TCP-abort verification remains applicable and all current lifecycle tests pass. Batch J does not change that path. |
| R24 — Raw stream unavailable in fallback | Resolved | DetailPane regressions pass; implementation is unchanged in this batch. |
| R25 — Active registry grows after capture stops | Resolved for the reported admission/drain paths | Writer give-up, stop-drain and admission controls pass. |
| Live-tab crash / 10k gate | Passed again | Same-tab workload, clear and post-clear reuse all converged; §5. |

### Earlier findings

These closures are carried forward after inspection of the Batch J diff and the current
complete suites. Earlier manual security/configuration exercises were not all repeated.

| ID | Status | Qualification |
| --- | --- | --- |
| R01 — Clean publish omits UI | Resolved | Clean publish/resource/SPA/asset/proxy smoke passed again. |
| R02 — Stale config routing | Resolved | Request-scoped routing remains; concurrency tests pass. |
| R03 — Captured Markdown makes requests | Resolved | Inert captured content, image mediation and CSP remain covered. |
| R04 — Settings focus loss | Resolved | Stable dialog lifecycle and scoped clocks remain; earlier interactive evidence is carried forward. |
| R05 — Decode allocation / invisible truncation | Resolved | Bounded decoding and body-local notices remain covered. |
| R06 — Queue accepts after writer stops | Resolved | Closed admission and command failure behavior remain covered. |
| R07 — Clear overtakes queued captures | Resolved | FIFO writer/clear ordering passes; R23 concerns client reconciliation afterward. |
| R08 — Partial response enrichment lost | Resolved | Interruption/provenance integration tests pass. |
| R09 — Ollama thinking/tools lost | Resolved | Adapter, golden and renderer tests pass. |
| R10 — Initial fetch loses completions | Resolved for the original completion race | Completion buffering remains effective. R23 is the separate stale-deletion response race. |
| R12 — Tags hide list | Resolved | The live tab remained usable with 100 tags; previous measured layout evidence is carried forward. |
| R13 — Backend rename collision | Resolved | Guard unchanged; previous interactive verification is carried forward. |
| R14 — Clear leaves stale selected detail | Resolved for the original same-tab selection case | Ack-driven detail eviction remains. Accepted cross-tab ID reuse is not reopened. |
| R15 — Null config causes 500 | Resolved | Validation/preservation tests pass. |
| R16 — Restart warning disappears | Resolved | Bound-listener/repeated-save tests pass. |
| R17 — Malformed render blanks app | Resolved | Validation, local boundaries and raw fallback pass. |
| R18 — Image preview missing | Resolved | Extraction and captured-image preview tests pass. |
| R19 — SSE EOF counted as blank line | Resolved | Parser/adapter terminal and incomplete-stream cases pass. |
| R20 — Intermittent stats rounding assertion | Resolved for the reported failure | Three further complete backend runs pass. |
| R21 — Failed save destroys config | Resolved | Atomic replacement/preservation tests pass; no power-loss durability claim. |
| R22 — SSE publication IDs reorder | Resolved | Ordered publish/fan-out and gap controls pass. R23 is an HTTP-fetch ordering issue, not SSE ID reordering. |

R26's closure concerns terminal lifecycle and error capture. It does not claim salvage
of upload bytes already consumed into a local injection-reader buffer after interruption.

## 2. Remaining findings

### 2.1 R23 — P2: the authoritative refetch can reuse the stale initial request

Locations: [the claimed new-fetch helper](E:/Code/Vessel/frontend/src/api/useLiveHistory.ts:224),
[clear calls it after the batch](E:/Code/Vessel/frontend/src/api/useLiveHistory.ts:318),
[recovery awaits it](E:/Code/Vessel/frontend/src/api/useLiveHistory.ts:431),
[the current regression timing](E:/Code/Vessel/frontend/src/api/useLiveHistory.test.ts:868).

J0 rule 4 says a clear or recovery always starts a new list fetch after the trigger and
that the last-started fetch wins. `refetchAuthoritative` implements this with
`queryClient.refetchQueries({ queryKey: REQUESTS_QUERY_ROOT })`.

TanStack Query v5 does not start a second fetch when the matching query's initial fetch
is still pending and has no data. In that state it returns the existing retryer's promise.
The helper therefore waits for the pre-trigger request instead of superseding it.

Two controlled sequences fail:

1. The initial list fetch takes a pre-clear snapshot and remains pending. A `cleared`
   frame arrives and its 100 ms batch flush runs; the DELETE acknowledgement also
   invalidates the list. After those refetch attempts, the held response returns row 1.
   **Actual final cache:** `[1]`. **Expected:** `[]`.
2. The same initial fetch remains pending while a completion and the clear frame are
   lost. A later frame exposes the ID gap; `/active` returns a post-clear snapshot at
   log position 3. Recovery performs its authoritative refetch, then the held pre-clear
   response returns row 1. **Actual final cache:** `[1]`. **Expected:** `[]`.

Both final-state probes fail identically on repeat. Fetch count remains one in each
case. This is the same pending-initial-response edge that earlier remediation tried to
close, now exposed beneath the new helper.

The repository regression does not exercise that timing. It emits the clear and then
immediately resolves the held fetch, before waiting for the 100 ms clear batch. The old
fetch therefore settles before the clear handler runs; the later purge/refetch makes the
test pass. Waiting beyond the batch before releasing the old response reproduces the bug.

**Required correction:** make the post-clear/post-recovery list read genuinely distinct
from any older unsettled request and prevent the old result from becoming authoritative.
Add both sequences above to the existing hook suite, including an assertion that the
second fetch has started before the held response is released. Preserve the J0 rule that
REST rows are not client-filtered; reintroducing ID or timestamp purges would reopen the
problems Batch J correctly removed.

### 2.2 R11 — P2: recovery cannot display an active request whose start was lost

Locations: [recovery intersects active IDs with known rows](E:/Code/Vessel/frontend/src/api/useLiveHistory.ts:408),
[the property test checks only false positives](E:/Code/Vessel/frontend/src/api/useLiveHistory.test.ts:1090),
[the documented display limit](E:/Code/Vessel/docs/phase-3.md:271).

The server snapshot correctly returns the authoritative active set, but its entries are
only sequence numbers. Rendering an in-flight row needs the method, path, backend, tags,
session and start time carried by `started`. Recovery therefore builds
`activeSeqs ∩ previously-known started details`; the code explicitly leaves any other
server-active request invisible until it completes.

Reproduced sequence:

1. Frame 1 establishes the client's event watermark.
2. `started(seq=2)`, frame 2, is dropped by the supported lossy subscriber queue.
3. `request_ready(seq=2)`, frame 3, exposes the gap while request 2 is still running.
4. Recovery receives `{ activeSeqs: [2], logPosition: 3 }` for the current server run.

**Actual UI in-flight set:** `[]`. **Authoritative server set:** `[2]`.

The result repeats. It can hide a long-running request for its entire duration. The
randomized property test does not cover this half of convergence: it proves only that
every row the UI *does* show is server-active, not that every server-active request is
shown.

This limitation is candidly recorded in Phase 3 and in Batch J's landing notes, but it
does not satisfy the brief/Phase 3 goal that the monitor show live in-flight requests
under the loss conditions the recovery protocol is designed to handle. It also qualifies
the architecture's statement that the client adopts `activeSeqs` wholesale.

**Decision required before implementation:** either enrich the recovery snapshot with
enough immutable display metadata to reconstruct active rows, or approve a placeholder
row and its interaction/detail behavior. If invisibility is the intended product trade,
R11 can close only after the brief, architecture and acceptance tests explicitly narrow
the live-monitor guarantee. This report does not choose among those product/API options.

## 3. Brief, architecture, plan and engineering assessment

The implementation remains aligned with the project's core shape: YARP forwarding,
bounded capture, a background SQLite writer, adapter enrichment and an embedded React
SPA. No replacement of those components is indicated.

Batch J improves the recovery design materially:

- `CaptureEvents` reads the active registry and publish position under one lock, so the
  snapshot has a meaningful point in the ordered event log.
- Client events retain SSE IDs; recovery discards queued/buffered evidence at or below
  the snapshot position and replays later recorded events in order.
- Clear frames carry no predicate and the client no longer deletes rows returned by REST.
  Clear-before survivors and reused SQLite IDs therefore come from the database rather
  than fragile client provenance.
- Event coalescing remains intact; the 10k same-tab gate passes again.

The exact four previous failures are fixed: a queued start cannot undo an accepted
snapshot; a queued pre-clear completion is discarded by position; a valid reused-ID REST
row is not purged when its completion is lost; and multiple missed clears converge through
the database refetch.

Documentation finding D04 remains partial for the two claims above. The
[Phase 3 REST rule](E:/Code/Vessel/docs/phase-3.md:266) and
[J0 contract](E:/Code/Vessel/docs/code-review-phase-4-plan.md:1094) say the trigger always
starts a new fetch, which the implementation does not guarantee. The Phase 3 display-limit
text is accurate, but the surrounding “wholesale replacement” language and the
[Batch J closing table](E:/Code/Vessel/docs/code-review-phase-4-plan.md:1243) overstate R11
closure against the product goal.

Testing is broad but the remaining seam is still the composition of independently timed
SSE arrival, the 100 ms batch, recovery and an already-pending HTTP query. The clear test
needs to let the trigger execute before resolving the old response. Lifecycle property
tests need equality with the authoritative active set, or an explicitly approved weaker
display contract, rather than only the current no-false-positive subset check.

Future replay, diff, copy-as-curl, cost estimates, Ollama panels and the release-platform
matrix remain outside Phase 4. No future-phase omission is treated as a defect here.

## 4. Verification performed

| Check | Result |
| --- | --- |
| `dotnet test --solution Vessel.sln` | 271 passed, 0 failed, 0 skipped ×3; durations 21.523 s, 21.688 s and 21.574 s. |
| `npm test -- --run` | 76 passed ×3 across 9 files; durations 17.10 s, 17.85 s and 17.27 s. |
| `npm run lint` | Exit 0; six existing warnings. |
| Focused production-hook probes | 3 failed ×2 with identical final states: received-clear stale initial response, recovered-clear stale initial response, and lost-start active false negative. A preliminary run also exposed all three before the assertions were strengthened to inspect final cache state. |
| Clean publish smoke | Passed from a fresh 327-file source copy containing no initial `dist`, `bin`, `obj` or `node_modules`: frontend build, self-contained single-file win-x64 executable, embedded resources, status, proxy path/body, unknown-backend 404, SPA and hashed JS asset. |
| Live browser gate | Passed: 10,000 streamed requests, clear to zero, then 100 post-clear requests in the same production tab. |

The published executable was approximately **102.9 MB**. The minified JS bundle was
**503.83 kB** and triggered Vite's 500 kB warning. Build success is not a warning-free
build. `npm ci` reported 245 packages and zero vulnerabilities in that install; this is
not a broader supply-chain or release-security audit.

The clean smoke used a prewritten isolated config, temporary database and ephemeral port.
It does not independently verify first-run default-config creation, restart persistence,
live-key remote providers or the multi-platform release matrix.

## 5. Live browser gate

A separate current VesselApp host served the production assets from the clean source copy,
using a temporary SQLite database and local streaming stub. No Vite development server was
used and the user's normal Vessel instance/data were untouched.

Workload: **10,000 requests, concurrency 24, 100 tags**, with 20 ms streamed-stub delays.
All requests were submitted successfully in **26.00 seconds**.

Observed in the same visible tab:

- At 4,864 persisted requests the UI showed 4,864 completed requests and a 17-request
  live pulse. Near completion it showed 8,704 and a 16-request pulse while draining its
  client event backlog.
- Three seconds later UI and API both showed **10,000 requests, 0 failures** and no live
  pulse. `/active` returned an empty active set at log position 40,000.
- Clear-all then removed all 10,000 synthetic requests. The same tab converged to zero,
  empty facets and the empty-state message.
- A further 100 requests exercised post-clear SQLite ID reuse. Three seconds later the
  tab showed **100 requests, 0 failures**, ten new tags and no live pulse. `/active`
  returned an empty set at log position 40,402.
- The captured browser warning/error log was empty. The tab stayed connected and never
  crashed, reloaded, navigated or was replaced.

Seven-sample local API medians during the 10k dataset were 1.36 ms for the list,
24.55 ms for FTS, 15.03 ms for a tag filter, 47.54 ms for facets and 12.24 ms for
stats. These are synthetic local observations, not production benchmarks. A model-filter
sample used the wrong model name and returned zero rows, so it is excluded.

This gate does not simulate an intentionally dropped `started` frame or keep an initial
list fetch pending past a clear. The focused probes exercise those interleavings. The
temporary host was stopped and the browser tab closed after verification.

## 6. Retained evidence

[Evidence index and reproduction commands](C:/Users/spenc/AppData/Local/Temp/vessel-phase4-round5-review/README.md)

[Focused frontend probe source](C:/Users/spenc/AppData/Local/Temp/vessel-clean-8a032ba11c6242f88b914f25ca52060b/frontend/src/api/round5-review.test.ts)

[First final-state probe run](C:/Users/spenc/AppData/Local/Temp/vessel-phase4-round5-review/frontend-probes-2.log)
and [identical repeat](C:/Users/spenc/AppData/Local/Temp/vessel-phase4-round5-review/frontend-probes-3.log)

[Backend run 1](C:/Users/spenc/AppData/Local/Temp/vessel-phase4-round5-review/backend-tests-1.log),
[run 2](C:/Users/spenc/AppData/Local/Temp/vessel-phase4-round5-review/backend-tests-2.log),
[run 3](C:/Users/spenc/AppData/Local/Temp/vessel-phase4-round5-review/backend-tests-3.log)

[Frontend run 1](C:/Users/spenc/AppData/Local/Temp/vessel-phase4-round5-review/frontend-tests-1.log),
[run 2](C:/Users/spenc/AppData/Local/Temp/vessel-phase4-round5-review/frontend-tests-2.log),
[run 3](C:/Users/spenc/AppData/Local/Temp/vessel-phase4-round5-review/frontend-tests-3.log)

[Lint log](C:/Users/spenc/AppData/Local/Temp/vessel-phase4-round5-review/frontend-lint.log),
[clean publish log](C:/Users/spenc/AppData/Local/Temp/vessel-phase4-round5-review/publish.log),
[10k workload log](C:/Users/spenc/AppData/Local/Temp/vessel-phase4-round5-review/seed.log),
[post-clear workload log](C:/Users/spenc/AppData/Local/Temp/vessel-phase4-round5-review/post-clear.log),
[browser observations](C:/Users/spenc/AppData/Local/Temp/vessel-phase4-round5-review/browser-observations.md)
