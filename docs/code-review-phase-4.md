# Code review through Phase 4 — verification of fixes

**Re-review date:** 28 August 2026

**Reviewed remediation:** `b4c5d63` (`Code review feedback`)

**Original review baseline:** `3db797a`

**Scope:** verify R01–R21 and D01–D05 against the brief, architecture, Phase 0–4 specifications, plan, and approved remediation decisions.

## Assessment

**Most fixes are effective, but the findings are not all closed.** Of the 21 original
findings, 17 original failure scenarios are resolved and four remain partially addressed
(R05, R09, R11, R18). Two additional regressions in the remediation need fixes:
out-of-order SSE publication IDs (R22) and buffered completions restoring cleared history
(R23). There are **six current P2 code findings**, detailed below. R05's original P1
unbounded-allocation problem is fixed; its remaining issue is a P2 display-integrity gap.

The backend suite passed **255/255 in three consecutive runs**, without failures or skips.
The existing frontend suite passed **25/25**, and its production build passed. Five
additional checks against the real frontend hook/renderers all failed; a separate C#
probe also confirmed out-of-order event IDs and lost Ollama generate thinking.

**Do not mark the Phase 4 acceptance gate complete yet.** The 10,000-row/100-tag layout
now works, but the browser renderer crashed during the live burst. That observation
does not establish its cause. Independently reproduced event-consistency defects are
sufficient to keep the live-history gate open.

This document replaces the old assessment in place; the original report remains in Git
history. This review changes only this report, not application code or repository tests,
and makes no commit. Separate edits to
[ui-spec.md](E:/Code/Vessel/docs/ui-spec.md),
[MessageView.tsx](E:/Code/Vessel/frontend/src/components/MessageView.tsx),
[MessageView.test.ts](E:/Code/Vessel/frontend/src/components/MessageView.test.ts), and
[StatsBar.tsx](E:/Code/Vessel/frontend/src/components/StatsBar.tsx)
appeared during the review and were left untouched. Published-build/browser evidence
below is for the isolated copy of `b4c5d63`, not those concurrent UI changes.

## 1. Status of every original finding

“Resolved” means the original defect has a corresponding code correction and the
evidence stated here. It is not a claim that every related edge case has been exhausted.

| ID | Status | Verification and qualification |
| --- | --- | --- |
| **R01 — Clean publish omits UI** | **Resolved; smoke caveat** | [Vessel.csproj](E:/Code/Vessel/src/Vessel/Vessel.csproj) builds the frontend before resource collection/compilation. A clean copy without dist/bin/obj/node_modules produced a 102.9 MB executable containing the UI. A separate launch served the SPA, JS and CSS assets, status, proxy traffic, and the unknown-backend error correctly. The complete smoke script did fail during restart; see §5. |
| **R02 — Stale live-config routing** | **Resolved** | [ConfigStore.cs](E:/Code/Vessel/src/Vessel/Config/ConfigStore.cs) publishes one config/version snapshot. [BackendRegistry.cs](E:/Code/Vessel/src/Vessel/Proxy/BackendRegistry.cs) resolves the requested snapshot and prevents cache regression; routing and limits use the same snapshot. Config concurrency and live-apply tests passed in all three backend runs. |
| **R03 — Markdown makes automatic requests** | **Resolved** | [MessageView.tsx](E:/Code/Vessel/frontend/src/components/MessageView.tsx) renders URL images as inert placeholders and links without navigable hrefs; control-plane CSP adds protection. In the browser, the captured Markdown/structured-image case initially had no image sources or links. Clicking the embedded image created only its captured data URI. Component and CSP tests passed. |
| **R04 — Settings steals input focus** | **Resolved** | [dialog.tsx](E:/Code/Vessel/frontend/src/components/ui/dialog.tsx) no longer reinstalls the focus lifecycle for changing callbacks; the clock is scoped to consumers. The numeric input retained its value and focus across subsequent updates. Typed DELETE enabled confirmation; the action was cancelled. |
| **R05 — Unbounded content decoding** | **Partial** | Streaming decode budgets and all-codec/stacked-encoding boundary tests fix the original allocation issue. The new read-time `decodeTruncated` flag is not consumed by the frontend; see §2.4. |
| **R06 — Writer gives up but queue accepts forever** | **Resolved** | [CaptureChannel.cs](E:/Code/Vessel/src/Vessel/Capture/CaptureChannel.cs) closes admission, releases queued/future commands with an error, and exposes stopped health. Writer resilience tests cover terminal failure and pending commands; API waits are cancellation-aware, and the health banner exists. |
| **R07 — Clear overtakes queued captures** | **Resolved** | [CaptureWriterService.cs](E:/Code/Vessel/src/Vessel/Capture/CaptureWriterService.cs) flushes preceding captures before executing a command. `ClearRunsAfterCapturesQueuedBeforeIt` passed, as did clear/FTS tests. This is separate from the new client-side R23 race. |
| **R08 — Transport errors discard partial response enrichment** | **Resolved** | Response provenance distinguishes Vessel-authored errors from captured upstream bytes. `ClientDisconnectMidStream_KeepsReassemblyResponseTextAndFts` and the pre-response-error exclusion test passed. Partial upstream content remains searchable and inspectable. |
| **R09 — Ollama tools/thinking lost** | **Partial** | Chat tool batches and thinking are accumulated, rendered and indexed; tests and the newly checked-in real Ollama fixture pass. The generate branch still loses thinking; see §2.5. |
| **R10 — Completion during initial fetch disappears** | **Original scenario resolved** | [useLiveHistory.ts](E:/Code/Vessel/frontend/src/api/useLiveHistory.ts) buffers completions while list fetches are unsettled and merges after settlement. Existing race tests pass. The new buffer does not respect a history clear: R23 remains a related regression. |
| **R11 — Lost events leave permanent running rows** | **Partial** | Reconnect/gap handling now refetches history, stats and facets, but only reconciles against cached history pages. A missed completion beyond those pages remains running. See §2.1; R22 also undermines the gap detector. |
| **R12 — Tags hide history list** | **Resolved** | [FilterBar.tsx](E:/Code/Vessel/frontend/src/components/FilterBar.tsx) caps the tag region and offers expansion; the list has a minimum height. At 1280×720 with 10,000 persisted rows and 100 tags, history retained a 391 px viewport both collapsed and expanded. Selecting tag 050 worked. |
| **R13 — Backend rename overwrites another** | **Resolved** | [ConfigPanel.tsx](E:/Code/Vessel/frontend/src/components/ConfigPanel.tsx) rejects case-insensitive collisions before mutating the draft. Renaming `review-second` to `OLLAMA` showed an inline error and kept both backend rows/default selection. No save was made. |
| **R14 — Clear leaves stale detail identity** | **Original same-tab scenario resolved** | [App.tsx](E:/Code/Vessel/frontend/src/App.tsx:64) removes detail queries and clears affected selection. The approved decision explicitly retains SQLite ID reuse without a schema migration; stale other tabs remain an accepted caveat. R23 is a distinct same-tab list-buffer defect, not that accepted caveat. |
| **R15 — Null config sections return 500** | **Resolved** | [ConfigLoader.cs](E:/Code/Vessel/src/Vessel/Config/ConfigLoader.cs) rejects null sections and null backend entries as validation errors. Loader and PUT tests passed, including preservation of existing config. |
| **R16 — Repeated save loses restart warning** | **Resolved** | The bound listener is recorded after startup and compared with desired configuration. GET includes pending restart state, so reopening the editor does not erase it. Repeated-save, listen-change and no-listen-change tests passed. |
| **R17 — Malformed messages blank app** | **Resolved** | [validate.ts](E:/Code/Vessel/frontend/src/render/validate.ts) validates normalized render data, and local error boundaries preserve raw fallback. A real captured request with an object-valued role opened as raw JSON; the viewer remained usable and another capture rendered normally. Validation/boundary tests passed. |
| **R18 — Image preview absent** | **Partial** | Image sources and click-to-preview exist for supported chat/provider blocks. A captured Ollama chat image previewed from a data URI without a remote source. Top-level Ollama generate images are still omitted; see §2.6. |
| **R19 — SSE EOF accepted as blank line** | **Resolved** | [SseParser.cs](E:/Code/Vessel/src/Vessel/Formats/SseParser.cs) distinguishes a real blank line from the synthetic final split item. LF/CRLF terminal matrices and adapter incomplete-stream warning tests passed. |
| **R20 — Intermittent stats assertion** | **Resolved for the reported rounding failure** | The deterministic seeded-duration test checks the average within `1e-9`; the live integration test uses an explicit `0.001 ms` tolerance instead of comparing separately rounded values. Counts/session/null/token assertions remain. All three full runs passed. This changes the comparison strategy; it is not an unchanged assertion or proof of unlimited suite stability. |
| **R21 — Failed save destroys valid config** | **Resolved** | Config saves write a temporary sibling and atomically replace/move into place before publishing the snapshot. Read-only-destination and successful-save cleanup tests passed. This verifies normal failure preservation, not power-loss durability. |

## 2. Remaining code findings

### R11 — P2: Reconciliation cannot remove completed requests outside loaded pages

**Locations:** [useLiveHistory.ts:135](E:/Code/Vessel/frontend/src/api/useLiveHistory.ts:135),
[RequestList.tsx:54](E:/Code/Vessel/frontend/src/components/RequestList.tsx:54).

The reconciliation path is described as authoritative, but its only evidence of completion
is the set of request identities present in cached list pages. Refetching a paginated
query refreshes its loaded pages; it does not enumerate all stored history or provide
the server's active-request set.

**Reproduction:** using the production hook with a real QueryClient/infinite query,
deliver `started` for request 1, omit its completion, then reconnect after 100 newer
requests have been stored. The refreshed first page contains requests 101 through 2
and points to an older page. Request 1 remains in `inFlight` after refetch settles.
The added assertion expecting it to leave fails.

The same limitation applies when loaded queries exclude a completed row by filters,
and a cleared, retained-away or unpersisted row cannot be found in history at all.
Repeatedly fetching the same first page will not resolve those cases. Session scoping
does not solve this lifecycle problem.

**Required outcome:** reconciliation must distinguish active from finished requests
independently of visible filters and pagination, while retaining legitimate long-running
requests. Choose and document the authoritative lifecycle mechanism; do not expire
legitimate requests by an arbitrary timer. Add page-boundary, filtered-history,
clear/retention, and long-running-request cases to the existing hook tests.

### R22 — P2: Concurrent publication breaks the SSE ID ordering assumption

**New regression in the R11 remediation.**

**Locations:** [CaptureEvents.cs:118](E:/Code/Vessel/src/Vessel/Capture/CaptureEvents.cs:118),
[useEvents.ts:71](E:/Code/Vessel/frontend/src/api/useEvents.ts:71),
[useLiveHistory.ts:229](E:/Code/Vessel/frontend/src/api/useLiveHistory.ts:229).

`Interlocked.Increment` makes IDs unique; it does not make ID assignment and channel
publication one ordered operation. Two publishers can allocate IDs N and N+1 and enqueue
them in the opposite order. The client treats forward jumps as loss and also moves
`lastId` backwards on a late lower ID, so later frames can cause further false gaps.

**Reproduction:** a C# probe published 100 batches of 128 events through the real hub,
with up to 16 concurrent publishers. Each batch was fully drained before the next;
the subscriber capacity is 256, so this test did not require dropping any frame.
All **12,800** events arrived, with **3,535 adjacent ID reversals**, including
`11 → 2` and `12 → 7`.

Each false gap can trigger another uncoordinated history/stats/facets reconciliation.
That creates unnecessary work during the exact burst the mechanism should recover from.
The existing monotonic-ID test sends requests sequentially and does not cover this.

**Required outcome:** give loss detection an ordering guarantee that matches actual
delivery, preserving the nonblocking proxy contract. Coalesce overlapping recovery
work. Test concurrent producers, complete delivery without false gaps, and real drops.
Do not treat a unique atomic counter alone as proof of ordered publication.

The browser crash during the 10k burst is recorded in §5. It has **not** been traced
to this defect and is not used as proof of that causal connection.

### R23 — P2: Buffered completions can restore a row after it was cleared

**New regression introduced by the R10 buffer; affects clear-history correctness.**

**Locations:** [useLiveHistory.ts:78](E:/Code/Vessel/frontend/src/api/useLiveHistory.ts:78),
[useLiveHistory.ts:120](E:/Code/Vessel/frontend/src/api/useLiveHistory.ts:120),
[DataPanel.tsx:28](E:/Code/Vessel/frontend/src/components/DataPanel.tsx:28),
[App.tsx:64](E:/Code/Vessel/frontend/src/App.tsx:64).

The clear handler invalidates list queries and evicts detail queries, but does not
invalidate `pendingRef`. When a pending list fetch settles, every buffered completion
is merged into the list, even if the clear has already deleted it.

**Reproduction:** keep the initial history query pending; deliver completion for row 1;
model a successful clear using the same request-list invalidation as DataPanel; resolve
the history query with an empty post-clear page. The production hook's cache becomes
`[1]`, not `[]`. This is a hook/cache reproduction of the race, not a claim that the
browser delete interaction was exercised.

The UI can therefore show deleted history again. Its detail may be missing, or a reused
SQLite ID may refer to a later capture. Evicting detail caches alone cannot prevent the
list resurrection.

**Required outcome:** clearing and live completion merging must share a deletion boundary
or generation model. Discard completions invalidated by the clear while preserving
requests that finish outside its scope. Cover clear-all and clear-before across both
initial fetch and refetch settlement, including captures that legitimately survive.

### R05 — P2 remainder: Read-time truncation is invisible in the viewer

**Locations:** [types.ts:38](E:/Code/Vessel/frontend/src/api/types.ts:38),
[PrettyJson.tsx:12](E:/Code/Vessel/frontend/src/components/PrettyJson.tsx:12),
[SqliteReadStore.cs:259](E:/Code/Vessel/src/Vessel/Storage/SqliteReadStore.cs:259),
[Summary.cs:52](E:/Code/Vessel/src/Vessel/Storage/Summary.cs:52).

The backend now returns `BodyPayload.decodeTruncated`, but the mirrored TypeScript type
has only `text` and `base64`. No frontend consumer handles the new flag. Capture-time
`body_truncated` warnings do not cover a later reduction of the display decode budget.

**API reproduction:** capture a gzip response expanding to 4 MiB with the 32 MiB limit,
then reduce `capture.maxBodyMb` to 1 and request the same detail again:

| Field | Before limit change | After limit change |
| --- | --- | --- |
| Decoded text length | 4,194,304 | 1,048,576 |
| Body `decodeTruncated` | false/omitted | true |
| Summary `truncated` | false | false |
| Summary warnings | empty | empty |

The API budget works. The viewer lacks the information needed to explain that its body
is now only a prefix. A component check rendering `{ text: 'partial body',
decodeTruncated: true }` found no truncation/limit warning.

**Required outcome:** add the field to the API mirror and display a body-local warning
in every applicable view, including raw response streams. Keep capture truncation and
display/decode truncation distinguishable. Test changing the cap after capture.

### R09 — P2 remainder: Ollama generate thinking is still lost

**Locations:** [OllamaAdapter.cs:87](E:/Code/Vessel/src/Vessel/Formats/OllamaAdapter.cs:87),
[OllamaAdapter.cs:119](E:/Code/Vessel/src/Vessel/Formats/OllamaAdapter.cs:119),
[ollama.ts:50](E:/Code/Vessel/frontend/src/render/ollama.ts:50).

The fix handles `message.thinking` for chat, but generate uses top-level
`thinking`. That is a documented Ollama response field.
[Ollama generate API](https://docs.ollama.com/api/generate),
[Ollama thinking documentation](https://docs.ollama.com/capabilities/thinking).

**Backend reproduction:**

```json
{"model":"review","thinking":"considering","response":"","done":false}
{"model":"review","response":"answer","done":true}
```

The real enricher produces `{"model":"review","response":"answer","done":true}`
and response text `answer`. The earlier thinking is absent from reassembly and
searchable text, though the raw stream is retained. Separately, providing a complete
generate response containing both `response` and `thinking` to the frontend renderer
produces no thinking block.

**Required outcome:** accumulate top-level generate thinking, retain it in the
synthesized response and intended search text, and expose it in the rendered response.
Extend the Ollama tests with generate, non-streamed, and interrupted-stream variants.
The real chat fixture is useful evidence, but does not exercise this branch.

### R18 — P2 remainder: Ollama generate images have no preview path

**Location:** [ollama.ts:26](E:/Code/Vessel/frontend/src/render/ollama.ts:26).

The generate request branch creates a block for `prompt` only. It never examines
top-level `images`, whereas chat's `requestMessage` now does. Ollama's generate API
supports an array of base64 images.
[Ollama generate API](https://docs.ollama.com/api/generate).

**Reproduction:** render an `ollama-generate` request containing a prompt and a valid
one-pixel PNG in `images`. The normalized view contains no image block; consequently
the existing safe preview component cannot be reached.

**Required outcome:** reuse the captured-image extraction/preview path for generate
requests, including an image with an empty prompt and malformed image entries. Retain
the no-network policy. This closes the remaining supported-format gap without changing
the approved image-preview design.

## 3. Contract and documentation decisions

The approval table in
[code-review-phase-4-plan.md](E:/Code/Vessel/docs/code-review-phase-4-plan.md:15)
is treated as the accepted contract, not as a fresh request for design approval.

| Item | Re-review |
| --- | --- |
| **D01 — Wire versus decoded storage** | Resolved at the storage boundary. Original compressed wire bytes are retained; enrichment/display decode bounded scratch data. Storage/API regression tests passed. R05 above is the remaining display warning issue. |
| **D02 — Non-streamed non-Ollama tok/s** | Resolved. The fallback based on whole-request duration was removed; null throughput is restored. Updated golden expectations reflect the approved contract. Exact Ollama evaluation-duration throughput remains valid. |
| **D03 — Host/browser-origin policy** | Implemented for the control plane. Host/Origin, hostile-host, cross-origin mutation, allowed loopback and proxy-exclusion tests passed. This is not authentication and does not promise safe public exposure. |
| **D04 — Acceptance/documentation accuracy** | Still open. The remediation plan checks the full 10k/100-tag gate while its own explanation says that combined case was not rerun. The reconnect gate is also overstated given R11/R22. Architecture §9.1 still describes separate snapshot/version publication rather than the atomic `ConfigSnapshot` unit. Correct the owning text and checkboxes in place. |
| **D05 — In-flight filters** | Implemented as approved: session scope applies; other active filters collapse in-flight rows to a count. Existing hook tests cover scoping/filter behavior. Correct presentation of that count still depends on fixing R11. |
| **R14b — SQLite identity reuse** | Accepted caveat, not reopened as a migration request. It does not justify R23's same-tab resurrection of cleared rows. |

The original architecture direction remains sound: YARP forwarding, capture tees,
off-path enrichment, a single SQLite writer, parameterized reads/FTS, provider adapters,
and the embedded React UI. The unresolved issues are at lifecycle and representation
boundaries, rather than evidence that the overall architecture needs replacing.

Replay, diff, copy-as-curl, pricing, live token tailing, release CI, the multi-platform
release matrix and the Phase 6 bind-address banner remain future-plan work. They are
not counted as Phase 4 defects.

## 4. Test coverage and engineering assessment

The remediation adds useful coverage at the right seams: concurrent config publication,
writer shutdown/FIFO, decompression budgets, real transport interruption, malformed render
models, and a real Ollama chat fixture. Those are material improvements over the
original review baseline.

Remaining coverage gaps correspond directly to the failures above:

- Reconciliation tests account for rows inside the refreshed page, not completed rows
  outside the loaded/filtered history or rows no longer persisted.
- The SSE ordering test does not use concurrent publishers.
- Buffer tests and clear/detail invalidation are tested separately rather than together.
- The hand-maintained API mirror omitted the newly added body flag.
- Ollama chat coverage does not establish equivalent generate behavior.

Extend those existing suites rather than adding tests that merely restate implementation
constants. Preserve the current passing tests and the failing regression cases until the
behavior is corrected. A green aggregate count alone cannot close these gaps.

The existing lint command completed with **six warnings**: virtualizer/compiler
compatibility and hook dependencies in RequestList, plus set-state-in-effect warnings
in App, DetailPane and ConfigPanel. These are follow-up maintainability concerns,
not additional demonstrated blockers in this re-review.

## 5. Verification actually performed

### Automated checks

| Check | Result |
| --- | --- |
| `dotnet test --solution Vessel.sln` | Three consecutive successful full runs: 255 passed, 0 failed, 0 skipped each; approximately 12.2, 13.8 and 11.6 seconds. First run used the workspace; subsequent runs used the isolated source copy. |
| `npm test` | Existing suite: 25 passed across five files. |
| `npm run build` | Passed TypeScript and production Vite build. |
| `npm run lint` | Completed with six warnings, not a warning-free result. |
| Additional frontend reproductions | Five assertions failed: off-page reconciliation, clear/buffer resurrection, decode warning, generate images and generate thinking. They were run outside the repository and were not skipped or converted to passing expectations. |
| C# concurrency/adapter probes | All 12,800 SSE frames received but 3,535 adjacent ID reversals; generate thinking absent from reassembly. |
| Read-time decode API check | 4 MiB capture read back as a 1 MiB prefix after lowering the cap, with body flag true but summary truncation false and no warnings. |

### Clean publish and executable checks

A copy containing tracked and nonignored source, but no build outputs or node_modules,
built the frontend **before** compiling the backend and produced the 102.9 MB win-x64
single-file executable. The assembly resource check found the SPA and assets.

The smoke script's assertions were preserved in an external copy; only source-root
selection and cleanup were adjusted to retain evidence. Its complete run **failed**:

- First launch created the default config, but its log also reported that port 4550
  was already in use. The script's first-run config assertion alone does not establish
  a successful first-run host startup.
- The second launch failed in SQLite initialization with `SQLite Error 10: disk I/O
  error`; the script then timed out waiting for status.
- The cause of that SQLite failure was not established. Existing user processes were
  not stopped to free the default port.

A **separate launch of that same published executable with a fresh temporary config
and database** succeeded on an ephemeral port. It returned status, served the SPA and
hashed JS/CSS with HTTP 200, proxied the synthetic traffic, and returned the expected
404/`unknown_backend` error. Thus R01's missing-resource defect is fixed, but this
is not a claim that the complete smoke script passed. A further restart command was
rejected by execution policy and provides no additional verification.

### Browser and 10,000-row exercise

The published executable accepted 10,000 synthetic requests at concurrency 24 in
approximately **5.07 seconds**; the API subsequently reported all 10,000 stored and
100 distinct tag facets. This is a local stress/seed exercise, not a production
throughput benchmark.

At 1280×720, a fresh browser tab over that seeded database retained a **391 px history
viewport** with the tag picker both collapsed and expanded. Tag 050 selected correctly.
Settings focus, duplicate backend-name rejection, typed confirmation without executing
deletion, malformed-role raw fallback, inert Markdown URLs, and an embedded image
preview were checked interactively.

However, the tab connected **during the live burst** reached the browser's
“This page crashed” screen. A fresh tab afterwards loaded the persisted history.
No browser crash dump or root-cause trace was obtained. Therefore the exercise verifies
storage and post-burst layout, **not** the uninterrupted live-view/zero-stuck-row gate.

Seven serial samples per endpoint on the seeded database gave:

| API operation | Median | Maximum |
| --- | ---: | ---: |
| First history page | 2.12 ms | 3.24 ms |
| FTS search for `needle` | 43.87 ms | 52.33 ms |
| Exact tag 050 | 19.85 ms | 21.61 ms |
| Model + successful status | 2.48 ms | 3.36 ms |
| Facets, including 100 tags | 61.00 ms | 74.64 ms |
| All-session statistics | 20.12 ms | 24.61 ms |

A live model/tool call and the under-ten-second Phase 4 litmus were not independently
rerun here. The checked-in real Ollama fixture was exercised by the backend suite.
This report does not substitute those fixtures for a fresh end-to-end model test.

## 6. Evidence retained locally

The review created only synthetic traffic and used temporary databases/configs. Logs and
probe source are retained outside the repository:

- [Backend test log](C:/Users/spenc/AppData/Local/Temp/vessel-phase4-rereview/backend-tests.log),
  [second run](C:/Users/spenc/AppData/Local/Temp/vessel-phase4-rereview/backend-tests-repeat.log),
  [third run](C:/Users/spenc/AppData/Local/Temp/vessel-phase4-rereview/backend-tests-third.log).
- [Final frontend regression output](C:/Users/spenc/AppData/Local/Temp/vessel-phase4-rereview/frontend-residuals-final.log)
  and [reproduction source](C:/Users/spenc/AppData/Local/Temp/vessel-clean-44bd313424bc476d88ca2c5c4f1d1fc7/frontend/src/api/review-residual.test.ts).
  These reuse the existing hook-test fixtures with real QueryClient behavior; they are
  separate from the repository's 25-test passing suite.
- [C# probes](C:/Users/spenc/AppData/Local/Temp/vessel-phase4-rereview/probes/Program.cs),
  [probe output](C:/Users/spenc/AppData/Local/Temp/vessel-phase4-rereview/backend-probes.log),
  [API probe output](C:/Users/spenc/AppData/Local/Temp/vessel-phase4-rereview/api-probes.log).
- [Publish log](C:/Users/spenc/AppData/Local/Temp/vessel-phase4-rereview/publish.log),
  [second-launch error](C:/Users/spenc/AppData/Local/Temp/vessel-smoke-9cc320337db44c69bef9c141ba4945dc/stderr2.log),
  [10k seed and timing output](C:/Users/spenc/AppData/Local/Temp/vessel-phase4-rereview/seed.log).

These temporary paths are review evidence, not permanent project dependencies. The
reproduction steps in §2 are sufficient to transfer the cases into the existing suites.

## 7. Conditions for closing this review

1. Resolve R11/R22 together: recover completed/abandoned lifecycle entries independently
   of visible history, and make gap detection correct under concurrent publication.
2. Resolve R23 and verify clear ordering across the writer, query cache and completion
   buffer as one interaction.
3. Surface decode truncation (R05) and finish generate thinking/images (R09/R18).
4. Keep all existing tests passing and add the missing regression cases.
5. Rerun the complete executable smoke, investigate the observed live-burst crash, and
   pass the literal 10k-row/100-tag live-history scenario without a reload workaround.
6. Correct D04's acceptance claims and atomic-snapshot description in their owning
   documents, then record only the gates actually demonstrated.

No application fixes or new design choices are implemented by this report.
