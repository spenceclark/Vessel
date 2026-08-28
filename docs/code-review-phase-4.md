# Code review through Phase 4

**Review date:** 28 August 2026

**Reviewed revision:** `3db797a`, with a clean tracked working tree at the start

**Scope:** implementation, tests, build tooling, product brief, architecture, UI design, and plan through Phase 4

## Assessment

The repository implements most of the planned Phase 0–4 feature surface and uses the
intended architecture. The proxy, capture pipeline, SQLite/FTS storage, format adapters,
and React viewer are recognisable, reasonably separated components. There is substantial
backend coverage, including streaming, redaction, retention, filtering, and live config.

**Phase 4 should not yet be treated as accepted.** A clean publish omits the UI; settings
inputs lose focus; live config can retain stale routing; and several capture and viewer
failure paths undermine the product's central promise of trustworthy inspection. The
10,000-row experiment also exposed a filter layout that can leave no space for history.

This report contains **21 actionable findings: six P1 and fifteen P2**. It also separates
unresolved design/documentation conflicts from implementation defects. No production code
or tests were changed, no tests were skipped or weakened, and no commit was made.

Severity means:

- **P1:** address before relying on this build for routine private traffic or declaring
  the Phase 4 gate complete; substantial functionality, privacy, or reliability risk.
- **P2:** a concrete correctness, resilience, acceptance, or usability defect requiring
  a scoped fix and regression coverage.

Source locations below are relative to the repository root. Line numbers refer to the
reviewed revision. Proposed remedies are recommendations, not approved design changes.

## 1. Requirement and architecture alignment

| Area | Assessment through Phase 4 | Evidence / qualification |
| --- | --- | --- |
| Transparent proxy and routing | Substantially implemented | YARP direct forwarding, route precedence, control-header stripping, streaming tees, and error mappings are covered by backend tests. Concurrent live routing still has R02. |
| Capture away from the request path | Substantially aligned | Enrichment, zstd storage, batching, and retention are on the writer. Terminal writer failure and decoded-body growth need R05/R06. |
| Timing and token metrics | Implemented, with a contract conflict | Ollama's exact metrics and streamed timing exist. Non-streamed throughput now uses total duration, contrary to Phase 2; see D02. |
| SQLite, WAL, FTS, retention | Substantially implemented | Parameterized filtering, literalized FTS terms, exact tag matching, and transactional FTS maintenance are good choices. Clear ordering and client identity need R07/R14. |
| Native Ollama support | Incomplete on important traffic shapes | Basic chat/generate and exact metrics work; streamed tools and thinking are lost in R09. This matters for the brief's primary backend. |
| OpenAI and Anthropic adapters | Broad implementation and fixtures | Streamed/non-streamed, usage, tools, and several malformed/truncated fixtures exist. Actual interrupted responses still fail R08; SSE EOF handling has R19. |
| Raw fallback / faithful capture | Not fully met | Proxy bytes and stored bytes are different concerns. Non-streamed compressed wire bytes are replaced in storage (D01); malformed rendered data can blank the UI (R17). |
| Embedded SPA / single executable | Clean publish fails the intended result | Frontend build succeeds, but the clean published assembly contains no UI resources (R01). |
| Live history and sessions | Implemented with consistency gaps | Initial-fetch races, lost lifecycle events, and stale detail identity are R10/R11/R14. |
| Phase 4 search and filters | API implementation is strong; UI gate not met | 10k-row API timings were reasonable locally, but 100 tag facets hide history (R12). Clarify treatment of in-flight rows under filters (D05). |
| Rendered messages, tools, cache/rate limits | Mostly implemented | Markdown, collapse, tool cards, rate-limit grouping, and cache counts exist. Privacy, malformed data, Ollama fidelity, and image preview need R03/R09/R17/R18. |
| Config editing and live apply | Not ready for acceptance | The feature exists, but focus, rename collisions, snapshot/version races, null validation, restart reporting, and persistence failure safety need fixes. |
| Clear-all / clear-before | Implemented but not consistently correct | Typed confirmation and transactional deletion exist. Queued captures can survive an earlier clear command, and deleted details can remain cached. |
| Local/private principle | Partially met | Default loopback binding and capture-side header redaction align with the design. Automatic Markdown resource requests violate the viewer's expected privacy boundary; Host/Origin policy needs an explicit decision. |

The following are **not missing Phase 4 work**: replay, diff, copy-as-curl, pricing,
context-growth charts, live response tailing, Ollama process/log panels, release CI,
licensing, the full multi-platform release matrix, and the Phase 6 bind-address banner.
Their absence is consistent with the plan. UI authentication is explicitly out of scope;
this review does not propose adding a login system.

## 2. Findings

### R01 — P1: A clean publish does not embed the frontend

**Locations:** `src/Vessel/Vessel.csproj:29–39`;
`src/Vessel/Api/StaticUi.cs:20–27`; `verify/publish-smoke.ps1:55–65`.

The resource glob is evaluated before `frontend/dist` exists, and `BuildFrontend` runs
before `PrepareForPublish`, after backend compilation. In an isolated copy containing
only tracked files, the documented self-contained publish command exited successfully.
Its log compiled `Vessel.dll` before running npm, and the resulting assembly had **zero
manifest resources**. `StaticUi` therefore takes its placeholder branch rather than
serving the SPA. An existing dist can mask the failure or embed an older frontend.

This violates Phase 3 D1, architecture §10, and Phase 4's publish gate. Generate the UI
before resource preparation/compilation and collect the generated resource items after
generation. Preserve the agreed npm-free ordinary backend build/test workflow. Verify
from a checkout with no dist/bin/obj/node_modules, and assert both the SPA shell and its
referenced asset from the published executable.

The clean executable itself was not launched during this review: that launch command
was denied by the execution policy. The build order and missing assembly resources were
verified directly; this is not a claim that the complete executable smoke ran.

### R02 — P1: A concurrent config save can leave routing permanently stale

**Locations:** `src/Vessel/Proxy/BackendRegistry.cs:79–92`;
`src/Vessel/Config/ConfigStore.cs:29–50`;
`src/Vessel/Formats/FormatEnricher.cs:55–74`;
`src/Vessel/Proxy/ProxyHandler.cs:62–80`.

The registry reads `Current`, builds its map, then reads `Version` separately. A PUT
between those reads lets a map built from snapshot A be labelled with snapshot B's
version. Subsequent lookups consider the old map current until another PUT. The registry
lock does not coordinate with `ConfigStore.Apply`. The enricher has the same pattern.

An isolated concurrency probe reproduced stale routing in three runs: the store reported
`http://127.0.0.1:11002`, while subsequent registry lookups still returned port `11001`
at version 2. The probe used 100,000 backend entries solely to widen the scheduling
window; that size is not a claim about normal usage. The interleaving also exists with
small configurations.

This breaks D7's next-request guarantee and can send prompts to the previous destination.
Publish snapshot and revision as one atomic state, or identify caches by the exact
snapshot they used. Publish each derived map/default pair together. Also resolve routing
and per-request limits from the same snapshot: they are currently separate reads.
Add controlled concurrent-save/rebuild coverage, including the enricher and in-flight
isolation. The mutable `VesselConfig` graph should not be described as intrinsically
immutable without enforcing or documenting ownership.

### R03 — P1: Rendering captured Markdown automatically makes network requests

**Locations:** `frontend/src/components/MessageView.tsx:91–99`;
`src/Vessel/Api/StaticUi.cs:47–53`.

`ReactMarkdown` uses its ordinary image rendering with no resource policy. A synthetic
captured prompt containing a Markdown image pointed at the local review stub generated
a new GET as soon as the Request tab rendered. No image click or approval was needed.
Only synthetic data and a local URL were used in the test.

The same rendering path accepts remote image URLs. Viewing an otherwise local capture
can therefore contact a third party and transmit whatever data is encoded in that URL.
This is a network side effect from untrusted captured content, not a demonstrated script
injection or raw-HTML XSS. It conflicts with the brief's local/private principle and with
Phase 4's deliberate image-placeholder interaction.

Disable automatic network-backed resources in captured Markdown. Agree a policy for
explicit previews of embedded/local image data, and apply a compatible CSP to the UI
routes as defence in depth without changing proxied response headers. Test that opening
captured content causes no unsolicited external or proxy-route requests.

### R04 — P1: The settings dialog repeatedly steals focus from its inputs

**Locations:** `frontend/src/components/ui/dialog.tsx:11–46`;
`frontend/src/components/StatsBar.tsx:137`;
`frontend/src/App.tsx:83`.

The dialog accessibility effect focuses its first control and depends on `onClose`.
`StatsBar` supplies a new inline callback on each render, while App's 250 ms clock
continually rerenders the tree. Each effect cleanup/setup restores and then reassigns
focus, effectively reopening the focus trap four times per second.

In the actual embedded UI, clicking the Max requests input was followed by focus moving
back to the Close button. This interferes with ordinary config typing and the Data
panel's typed delete confirmation even when there is no active request.

Stabilize callback identity and make initial focus/restoration follow the dialog's
open/close lifecycle, not timer-driven renders. Verify typing a multi-character value
and `DELETE`, keyboard tabbing, Escape, and focus restoration while the clock and SSE
updates continue. Keep the confirmation requirement intact.

### R05 — P1: Content decoding has no output budget

**Locations:** `src/Vessel/Formats/BodyDecoder.cs:33–98`;
`src/Vessel/Formats/FormatEnricher.cs:100–116`.

The capture cap bounds compressed wire input, but gzip/deflate/Brotli decoding copies to
an unbounded `MemoryStream`; zstd uses an unbounded `Unwrap`. Parsing, string creation,
and storage then operate on the expanded result. No decoded-byte budget reaches the
decoder, and multiple content encodings can compound expansion.

With `maxBodyMb = 1`, a **2,082-byte** gzip body produced a **2,097,163-byte** stored body,
with `Truncated = false`. The test deliberately used a small safe expansion; process
memory exhaustion was not attempted. Larger or repeated inputs can exhaust the writer's
memory and threaten the shared proxy process despite the advertised cap.

Agree whether decoded data shares the capture cap or has a separate explicit budget.
Enforce it while decoding, before allocating the entire result, including stacked
encodings and zstd. Preserve bounded wire evidence and expose the chosen truncation/
decode-limit outcome. Test both large expansions and normal compressed captures. D01
below separately addresses the loss of original compressed bytes.

### R06 — P1: Writer give-up leaves an accepting queue with no consumer

**Locations:** `src/Vessel/Capture/CaptureWriterService.cs:84–86,164–177`;
`src/Vessel/Capture/CaptureChannel.cs:13–20`;
`src/Vessel/Api/RequestsEndpoints.cs:86–88`;
`src/Vessel/Api/SessionsEndpoints.cs:39–41`.

After five consecutive capture failures, the writer returns. The unbounded channel
remains open, future captures keep entering it, and clear/session commands await
completion sources that nobody will resolve. HTTP request cancellation does not bound
those waits. A log message is the only indication that recording stopped.

A fault-injected store reached the five-failure threshold, then accepted a clear command
whose completion remained unresolved. Subsequent traffic can accumulate retained bodies
without limit, eventually affecting forwarding itself.

The existing drop/five-failure policy is documented; the defect is the terminal state
around it. Close or fault admission, resolve/fail queued commands, and expose capture
health. Keep forwarding non-blocking under the agreed failure policy. Add tests for
commands queued before and after give-up, new captures, cancellation, and shutdown.

### R07 — P2: Clear commands overtake earlier captures in the same writer batch

**Location:** `src/Vessel/Capture/CaptureWriterService.cs:123–153`.

`Flush` collects captures but executes clear commands immediately, inserting all collected
captures only after processing the commands. Consequently, a capture queued before a
clear can be inserted after that clear has reported success.

The deterministic probe enqueued `/before-clear`, then `ClearCommand`, before starting
the writer. Clear returned `deleted: 0`, and `/before-clear` remained afterward. This
also affects clear-before when an eligible old capture is still in the batch. Existing
clear tests wait for persistence first and do not exercise this ordering.

Preserve FIFO operation order by flushing preceding captures at command boundaries.
Test capture → clear → capture in one batch, including FTS and delete counts. Whether
requests still in flight when the user clears should later appear is a separate product
decision; this finding concerns captures already ahead of the command in the queue.

### R08 — P2: Real partial responses are discarded by enrichment on transport errors

**Locations:** `src/Vessel/Formats/FormatEnricher.cs:126–139`;
`src/Vessel/Proxy/ProxyHandler.cs:261–266`.

`hasRealResponse = record.Error is null` treats every proxy error as if no upstream
response existed. That is appropriate for a connection failure before headers, but not
for a disconnect after streamed content arrived. The adapter receives no response text,
so partial reassembly, response search text, and available response metadata disappear.

The same captured SSE fragment produced `partial answer` and `stream_incomplete` without
an error, but no response text or reassembled body with `ResponseBodyDestination` or
`client_disconnect`. Raw captured bytes remained available; the failure is in the useful
derived view and search index. This contradicts Phase 2 D4's partial-stream behaviour.

Distinguish locally generated pre-response errors from a real, interrupted upstream
response. Preserve the transport warning while parsing whatever bounded upstream bytes
were received. Extend the disconnect integration test to assert reassembly and FTS,
alongside the existing byte-capture assertions.

### R09 — P2: Ollama stream folding loses later tool calls and thinking

**Locations:** `src/Vessel/Formats/OllamaAdapter.cs:64–125`;
`src/Vessel/Formats/TextFlattener.cs:242–249`;
`frontend/src/render/ollama.ts:42–62`.

The adapter accumulates `message.content` but remembers only the first non-null
`tool_calls` array via `??=`. Later tool-call batches are ignored; an initial empty array
can also mask subsequent calls. There is no thinking accumulator, and the Ollama view
does not render thinking even when a non-streamed response contains it.

A two-chunk probe with tool calls `one` and `two` plus thinking text synthesized only
`one` and omitted thinking. The raw stream survives, but the default response view and
derived searchable content are incomplete. Ollama documents that streamed content,
thinking, and tool-call fields must be accumulated across chunks.
[Ollama streaming documentation](https://docs.ollama.com/capabilities/streaming).

Fold all applicable partial fields according to their wire shape, including empty first
arrays and multiple calls. Preserve thinking in the synthesized object and render it
collapsed. Extend the existing Ollama fixtures rather than replacing their assertions;
verify the raw stream, synthesized response, and displayed tool exchange together.

### R10 — P2: Completion during the initial list fetch can disappear indefinitely

**Location:** `frontend/src/App.tsx:63–76`.

The code assumes `invalidateQueries` queues another fetch behind an active fetch. With
the installed TanStack Query implementation, an initial fetch with no cached data reuses
the existing promise instead. A completion can therefore be omitted from the initial
database snapshot and never inserted or fetched afterward.

A probe using the installed `QueryClient` and active `QueryObserver` delayed the first
request, invalidated while it was fetching, and then resolved the old empty snapshot.
The result was **one query call, zero rows, and `isInvalidated: false`**. This is the
initial/no-cached-data case; it does not claim all refetches behave identically.

Buffer/merge completions across fetch settlement, or explicitly cancel and start a fetch
whose snapshot is newer than the completion. Cover initial load, reconnect, scope/filter
changes, and paging. Update the incorrect queuing explanation in App and the Phase 4
report when fixing it. A later request or manual reload is not a reliable repair.

### R11 — P2: Lost lifecycle events leave permanent in-flight rows

**Locations:** `src/Vessel/Capture/CaptureEvents.cs:20–33`;
`frontend/src/api/useEvents.ts:38–46,79–86`;
`frontend/src/App.tsx:50`.

Subscriber queues intentionally drop oldest events when full. The client only removes
an in-flight entry upon `completed`; reconnect refreshes requests/stats but never
reconciles that map. There is also no loss signal that triggers recovery while the SSE
connection stays open. Facets are not invalidated on reconnect.

During the synthetic 10k-request burst, all requests finished and the API/stats reported
10,000 stored rows, but the browser still showed running entries with 53-second timers
and at least 21 visible in-flight rows. Reload cleared them. This is a stress case, not
a claim about usual Ollama throughput; a lost completion during reconnect has the same
unreconciled state by source inspection.

Keep the non-blocking broadcast policy, but add an authoritative reconciliation path
or detectable event-gap mechanism. Recover completed history, in-flight state, and
facets together. Do not expire genuinely long LLM requests solely by an arbitrary timer.
Test a dropped completion both with and without an EventSource reconnect.

### R12 — P2: The supported number of tag facets can hide the entire history list

**Locations:** `frontend/src/components/FilterBar.tsx:143–163`;
`frontend/src/App.tsx:103–105`.

Every facet tag renders in an unbounded wrapping area above the list. The parent has a
fixed viewport height and hides overflow, so tags consume the list's available height.
The API's cap of 100 is not a usable layout bound.

With 10,000 rows and 100 distinct tags at 1280 × 720, the reloaded embedded UI displayed
tag chips down the history panel, clipped the final tags, and exposed no request-row
buttons. Search does not reduce these session-wide facets. The user cannot complete
the Phase 4 find-and-read task in this state, regardless of SQL speed.

Agree a bounded tag-picker interaction: for example, a scrollable picker or collapsed
selection surface. Preserve a minimum usable history area and test 0, 1, and 100 facets
at normal laptop heights, with long names and active filters. This needs a UI design
decision rather than an arbitrary visual change during a bug fix.

### R13 — P2: Renaming a backend onto an existing name silently overwrites it

**Location:** `frontend/src/components/ConfigPanel.tsx:36–45`.

`renameBackend` deletes the old dictionary entry and assigns it to the new key without
checking whether that key already exists. After the overwrite, the server cannot detect
the original duplicate because only one property remains in the submitted JSON.

In the browser, renaming `review-second` (OpenAI type) to the existing `ollama` name
reduced two rows to one OpenAI row with no error. The draft was cancelled, not saved.
Saving that draft would lose the other backend and could repoint default traffic.

Reject collisions before modifying the draft, using the server's case-insensitive name
semantics while allowing a legitimate case-only rename of the same backend. Add UI
coverage for duplicate names, default-backend references, and preservation of both
configurations after a rejected rename.

### R14 — P2: Clearing history leaves cached details, and SQLite can reuse their IDs

**Locations:** `src/Vessel/Storage/SqliteCaptureStore.cs:41`;
`frontend/src/components/DataPanel.tsx:33–37`;
`frontend/src/components/DetailPane.tsx:40–44`;
`frontend/src/App.tsx:25`.

Clear invalidates `['requests']`, stats, and facets, but not `['request', id]`, and it
does not clear selection. A selected deleted capture can remain visible. Ordinary
`INTEGER PRIMARY KEY` also reuses IDs after the table is emptied: the storage probe
inserted ID 1, cleared, then inserted a different capture with ID 1.

With that same ID still selected, the detail query key does not change; clicking the
new row can retain the old capture's body. ID reuse was reproduced; the cache consequence
was established from the client state/query flow, not a separate end-to-end browser
reproduction. SQLite explicitly distinguishes ordinary ROWID allocation from guaranteed
non-reuse. [SQLite AUTOINCREMENT documentation](https://www.sqlite.org/autoinc.html).

Clear selection and affected detail caches on deletion. Agree whether capture identity
must be non-reusing across clears, or whether a database generation belongs in client
identity. A schema change needs an explicit migration decision; do not simply change
new-database DDL and leave existing databases different. Test clear → new capture →
select, including clear-before and a currently selected deleted row.

### R15 — P2: Structurally invalid JSON config produces a 500 instead of validation

**Locations:** `src/Vessel/Config/ConfigLoader.cs:70–144`;
`src/Vessel/Api/ConfigEndpoints.cs:28–57`.

Non-nullable C# declarations do not prevent JSON from setting nested objects or backend
entries to null. The validator dereferences those values before issuing a
`ConfigException`. `PUT {"backends":null}` returned HTTP 500 in the isolated server;
null backend entries and null retention also produced `NullReferenceException` in the
production validator.

This violates D7's invalid-config → 400/human-message contract, and the same input can
bypass startup's intended configuration-error handling. Validate required object
structure before its members, including backends, entries, retention, capture, warnings,
and timeouts. Extend startup and PUT tests, retaining checks that neither current state
nor the saved file changes after rejection.

### R16 — P2: A second config save clears a still-required restart warning

**Location:** `src/Vessel/Config/ConfigStore.cs:46–52`.

Restart detection compares the candidate listener with the most recently saved config,
not the address Kestrel actually bound. Changing `listen` reports `['listen']`, but saving
that same config again reports `[]` even though the process is still on its old address.
The production `ConfigStore` probe reproduced both results. The UI's result banner is
also local panel state and does not survive reopening the panel.

Track effective startup listener state separately from desired persisted config and
report the pending restart until they match. Make that state available when reopening
settings. Test repeated saves, unrelated edits after a listener change, reverting to
the active address, and restart. This is distinct from the Phase 6 non-loopback banner.

### R17 — P2: A malformed captured message can blank the whole application

**Locations:** `frontend/src/render/openai.ts:41–60`;
`frontend/src/components/MessageView.tsx:59–65`;
`frontend/src/main.tsx:18–24`.

Extractors cast arbitrary captured JSON into a typed view without validating field
shapes. Their try/catch only surrounds extraction; it cannot catch later React rendering
errors. There is no surrounding error boundary that preserves the raw view.

A captured request with `messages: [{"role":{"unexpected":"object"},"content":"hello"}]`
proxied successfully to the review stub. Selecting its Request tab made the embedded UI
blank, with an empty document body text. The malformed role reached React as an object
child. Other `any`-typed text/tool fields present similar validation risks; those variants
were not all tested.

This violates D4's extraction-failure fallback and the brief's graceful degradation.
Validate the normalized view model at the untrusted-data boundary and retain a local
render-error fallback to raw JSON so one capture cannot remove the entire viewer. Add
malformed-role/text/tool cases and verify that navigating other captures still works.

### R18 — P2: The specified image preview has no implementation path

**Locations:** `frontend/src/render/types.ts:5–10`;
`frontend/src/render/openai.ts:51–54`;
`frontend/src/components/MessageView.tsx:85–86`;
`frontend/src/render/ollama.ts:68–83`.

Phase 4 D4 specifies an image placeholder that can be clicked to view already-local
image data. The normalized image block retains only a label, and rendering produces a
non-interactive badge. OpenAI image sources are discarded by extraction, and Ollama's
message images are not represented at all. This is a source-confirmed feature gap, not
a claim that the underlying capture bytes are lost.

Retain a safe reference to captured image data and implement the agreed explicit preview
interaction together with R03's network policy. Cover embedded image data, malformed
images, Ollama image arrays, and remote URLs without silently fetching them. If preview
is deliberately deferred, obtain agreement and correct D4's scope in place.

### R19 — P2: SSE parsing accepts an event missing its final blank line

**Location:** `src/Vessel/Formats/SseParser.cs:23–33`.

Splitting a string ending with a single newline produces a trailing empty array entry,
which is treated as an actual blank line. The probe returned one event for
`data: [DONE]\n` as well as for `data: [DONE]\n\n`. Only the second has the required event
terminator. An incomplete terminal event can therefore suppress `stream_incomplete`.

The parser's own contract and Phase 2 D4 require incomplete final events to be discarded.
The HTML standard also requires pending event data to be discarded at EOF without the
final empty line. [WHATWG event-stream interpretation](https://html.spec.whatwg.org/multipage/server-sent-events.html#event-stream-interpretation).

Distinguish a split sentinel at EOF from an actual empty input line. Extend the existing
SSE tests with LF/CRLF, no newline, one newline, and two newlines, and assert the adapter's
terminal-warning result as well as parser event counts.

### R20 — P2: The backend suite has a reproduced intermittent stats failure

**Location:** `tests/Vessel.Tests/ApiTests.cs:144–156`.

One full run failed `Stats_TotalsAndAverages_SessionScoping` at line 155: expected
`18.820500000000003`, actual `18.820499999999999`. The three-decimal assertion rounded
the values to opposite sides of `18.8205`. A full rerun passed all 181 tests.

The fixture derives its average from real measured durations and compares independently
aggregated floating-point values at a rounding boundary. This appears to be fixture/
numeric nondeterminism rather than evidence of a meaningful latency error, but a rerun
does not make the failing acceptance gate reliable.

Make the aggregate fixture deterministic with known stored durations that retain the
intended precision coverage, and keep separate integration assertions for live capture
timings/session scoping. Investigate the calculation contract before changing it. Do
not remove, skip, loosen the accuracy requirement, or treat reruns as the fix.

### R21 — P2: Config persistence can destroy the last valid file during a failed save

**Locations:** `src/Vessel/Config/ConfigLoader.cs:48–52`;
`src/Vessel/Config/ConfigStore.cs:48–50`.

Every live save truncates and rewrites `vessel.json` using `File.WriteAllText`. A write
failure or process termination after truncation can leave an empty/partial file. The
in-memory snapshot is not swapped until after save, but the next startup can no longer
load the last valid configuration. This is a source-level failure-safety finding; disk
exhaustion and process termination during save were not induced.

Persist to a temporary file in the same directory and replace the destination only after
the write has succeeded, with cleanup and actionable errors. Specify the durability
expectation rather than promising power-loss safety from a rename alone. Add injected
write/replace-failure coverage proving both the old file and active snapshot survive.

## 3. Decisions and documentation that need reconciliation

### D01 — Wire capture versus decoded storage is an unresolved contract change

Phase 2 D3 and the capture architecture describe decoding as scratch work while retaining
wire evidence. `FormatEnricher.cs:108–116` instead replaces a non-streamed compressed
`ResponseBody` with decoded bytes, leaves its captured Content-Encoding header unchanged,
and does not retain the original body separately. The compression probe confirmed
`wirePreserved: false` and `ResponseRaw: null`.

This is deliberate in `FormatEnricherTests.CompressedResponse_NonStreamed_DecodedForStorage`,
so it should not be silently reverted or its test weakened. Agree whether storage must
preserve original non-streamed wire bytes alongside a decoded display body. If it must,
define the API/storage representation and migration implications. If decoded-only is
accepted, correct the raw/fidelity promises and make the distinction visible in the UI.
Either way, R05's decompression budget still needs fixing.

### D02 — Non-streamed tok/s now measures a different quantity

Phase 2 D6/F6 requires null for non-Ollama non-streamed throughput. Architecture §4.2
defines wire-span throughput, with Ollama's exact rate preferred. The implementation at
`FormatEnricher.cs:173–209` now uses output tokens divided by whole-request duration for
non-streamed responses, and tests explicitly require that fallback.

That number includes request transfer, queueing, prefill, and network time, so it is not
the same measure as generation throughput. It enters the same UI/session average without
a metric-source distinction. Agree whether to retain it with an explicit definition and
label, distinguish metric sources, or restore the earlier contract. Update architecture,
Phase 2 acceptance text, and tests consistently after that decision.

### D03 — Loopback binding needs a documented browser-origin threat model

The API accepts an arbitrary Host header: a synthetic request with `Host: review.invalid`
to the local review server returned config with HTTP 200. No application Host/Origin
policy was found in `VesselApp.cs:26–77`. Kestrel binding itself does not validate Host.
[Microsoft Kestrel host-filtering guidance](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/kestrel/host-filtering?view=aspnetcore-10.0).

This confirms missing server-side validation, **not a completed browser DNS-rebinding
exploit**; browser local-network protections and deployment conditions affect exposure.
Given readable prompts and config/deletion APIs, agree an accepted-host policy and
same-origin checks for control-plane mutations, scoped so ordinary SDK proxy traffic is
not accidentally broken. This does not require UI authentication or moving Phase 6's
network-binding banner into the current scope.

### D04 — Acceptance status and current documentation overstate or misdescribe the tree

- `docs/phase-4-report.md:3–5` calls the phase complete while its final paragraph leaves
  the 10k exercise and under-ten-second litmus outstanding. Its acceptance table marks
  manual gates done even though Phase 4 §5 explicitly includes those exercises. Report
  implementation status separately from acceptance status.
- That report says `verify/publish-smoke.ps1` has its own `npm ci && npm run build` step.
  The current script invokes `dotnet publish` directly; it does not have that separate
  preparatory step. R01 explains why previous dist output can hide a broken clean build.
- Its explanation that query invalidation queues a second fetch is not true for the
  initial-fetch case in R10. Correct that explanation when the implementation is fixed.
- `README.md:12–15` still says Phase 0 and describes capture/UI as future work.
  Architecture §12 still lists .NET 9 while the project and plan use .NET 10.
- The current tree contains an OpenAI Responses adapter, renderer, fixtures, and later
  UI/SSE/token-total additions. Record their accepted scope and current contracts in the
  relevant documents; do not imply the older 167-test report describes today's
  181-test tree without qualification.

These are recommendations for subsequent edits to the affected text in place. This
review only adds the requested report; it does not append correction notes to the older
documents or silently change their approved requirements.

### D05 — Clarify which filters apply to in-flight rows

`RequestList.tsx:63` prepends every entry from the global in-flight map regardless of
backend/model/tag/session filters. `StartedEvent` does not supply session identity, so
session scoping cannot currently be applied accurately there. Completed rows are scoped
separately. Status/warnings are also not final while a request is running.

Decide whether pending rows are intentionally a global activity area or should obey the
available filters. Make the UI distinction explicit and define unknown-field behaviour.
Do not quietly invent semantics while fixing R11. Similarly, the API supports a specific
historical session, while the visible switch only exposes Current/All; confirm whether
that is the intended extent of Phase 4's session filter.

## 4. Best-practice assessment

### Choices worth preserving

- The stack and dependency footprint fit a local inspection proxy. There is no need
  for a gateway framework, distributed storage, or a wholesale rewrite.
- Routing/capture/enrichment/storage/API/UI are separated sufficiently to test the
  critical contracts. Most new fixes have a natural home in existing test files.
- Captured authorization-related headers are redacted before entering the writer queue,
  while the forwarded request remains unaffected. Full prompt storage is intentional.
- FTS terms are quoted, SQL values are parameterized, tags use JSON element matching,
  and FTS deletes share transactions with request deletes. Preserve these properties.
- Lifecycle broadcast is bounded and non-blocking. R11 needs recovery around deliberate
  event loss, not blocking the proxy for a slow browser.
- Source-generated API serialization, fixture-driven adapters, virtualized history,
  locally bundled fonts, and separate dev/embedded hosting are sensible choices.
- Declared private fields generally follow the house `_camelCase` convention. Style
  churn is not a priority relative to the functional defects above.

### Further engineering risks, not additional reproduced defects

- The capture queue is unbounded by explicit design. Even after R06, sustained producer
  throughput above writer throughput can accumulate bodies. Define an overload policy
  with the user; bounded memory, never dropping captures, and never delaying forwarding
  cannot all be guaranteed under arbitrary sustained overload. Do not silently change
  the existing policy in a resilience patch.
- Config/facet/detail/list loading failures are often presented as empty or perpetual
  loading states. For example, `DetailPane.tsx:64` displays Loading when no data exists
  even after an error. The connection state returned by `useEvents` is not consumed by
  App. Add visible, recoverable error/stale states after agreeing their presentation.
- App's clock rerenders the complete subtree every 250 ms even with no active request.
  Beyond R04, profile a large rendered conversation and isolate timer updates to active
  rows/details. The current short-body benchmark does not establish large-context UI
  performance.
- The hand-written tab primitives need a keyboard/accessibility pass in addition to
  dialog repair. Library-like class names do not guarantee keyboard navigation or
  tab/panel relationships. A full accessibility audit was not performed here.
- Golden fixtures are useful but predominantly synthetic. Preserve them and add selected
  current provider captures, especially multi-tool/thinking Ollama traffic and interrupted
  streams. Live-key verification remains optional and was not run in this review.

## 5. Verification performed

All additional traffic used isolated temporary config/database files and a local stub.
The existing test fixtures were reused by an external probe project referencing the
production project. No user's history was cleared. The config collision was tested only
in an unsaved draft. The temporary server was stopped after the browser checks.

### Build and test results

| Check | Result |
| --- | --- |
| Environment | Windows; .NET SDK 10.0.301; Node 24.16.0; npm 11.13.0 |
| `dotnet test --solution Vessel.sln` | 181 total: 180 passed, 1 failed, 0 skipped; R20 records the exact failure |
| `dotnet test --no-build --solution Vessel.sln` | 181 passed, 0 failed, 0 skipped; approximately 11.2 seconds |
| `npm run build` in frontend | Passed TypeScript compilation and Vite production build |
| `npm run lint` in frontend | Exit 0, with 7 warnings; not a warning-free result |
| Clean tracked-file publish | Command succeeded; compiled assembly had zero embedded resources (R01) |
| Production NuGet vulnerability listing, including transitive packages | No known vulnerable packages reported by the configured source at review time |
| npm audit output during clean dependency installation | Zero reported vulnerabilities at review time |

The seven lint warnings concern hook/ref/dependency patterns in `useEvents`, TanStack
Virtual compatibility/dependencies in `RequestList`, and effect-driven state in
`ConfigPanel`/`DetailPane`. They are not proof of the correctness of those components;
the browser defects above require behavioural tests.

The clean publish command was:

```powershell
dotnet publish src/Vessel -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

It ran in a separate copy without ignored build products. The original frontend build
was used for the browser review, not the empty-resource clean publish. Dependency audits
are useful baselines, not security certifications.

### Focused probes

| Probe | Observed result |
| --- | --- |
| Capture queued before clear, same writer batch | Clear deleted 0; earlier capture subsequently persisted |
| Five injected writer failures, then command | Writer stopped; queue accepted command; completion remained pending |
| Identical partial SSE with/without transport error | Error-bearing variants lost reassembly and response search text |
| Two Ollama tool chunks plus thinking | Only first tool call retained; thinking absent |
| 1 MB cap, 2,082-byte gzip input | 2,097,163 decoded/stored bytes; no truncation; original response not retained |
| Config rebuild overlapping save | Old URL remained cached despite new current config, in 3 widened-window runs |
| Null config structure | HTTP 500 for null backends; validator null-reference exceptions for nested variants |
| Listener change followed by identical save | Restart required first time, incorrectly absent second time |
| Clear then new row | ID 1 reused |
| SSE terminal event with only one final newline | Incorrectly accepted as a complete event |
| Query invalidation during first pending fetch | One query invocation, stale empty result, no queued replacement |
| Render captured Markdown image | Automatic GET to synthetic local image URL |
| Rename backend onto another name | Existing draft entry overwritten without validation |
| Focus config number input | Focus moved to dialog Close button on timer-driven rerender |
| Open malformed-role request | Viewer became blank |

### 10,000-row exercise

The local stub accepted 10,000 short synthetic requests at 24-way concurrency in about
4.08 seconds; all 10,000 were subsequently stored. This is a stress/seed exercise, not
a realistic model-generation workload. There were 100 distinct tags.

Seven sequential HTTP samples per endpoint produced these local timings:

| Endpoint / filter | Median | Maximum observed |
| --- | ---: | ---: |
| Request list, 100 rows | 2.15 ms | 5.15 ms |
| FTS query `needle`, 100 rows | 34.04 ms | 35.74 ms |
| Exact tag filter | 15.10 ms | 17.04 ms |
| Model + status filter | 2.12 ms | 2.39 ms |
| Facets, including 100 tags | 77.79 ms | 112.93 ms |
| All-session stats | 31.80 ms | 60.42 ms |

The API timings are encouraging for this data. They do **not** certify UI smoothness or
the under-ten-second findability goal: the browser exposed both lingering in-flight
entries and the tag layout failure. No percentile or cross-machine performance claim is
made from seven samples.

### Limits of this review

This was a broad source and executable review, not a proof that every path is correct.
It did not complete a day-long real-traffic soak, run live OpenAI/Anthropic calls, validate
all provider versions, execute every RID/trimming combination, induce disk/power loss,
measure huge-context rendering, or perform a full accessibility/security penetration
test. It did not reproduce a complete DNS-rebinding exploit. The clean published
executable launch was policy-blocked, as described in R01. Temporary probes are review
evidence, not additions to the maintained regression suite.

## 6. Recommended next steps and acceptance gate

1. **Repair the P1 foundation first:** clean publish, atomic config/cache state, Markdown
   resource policy, dialog focus, bounded decoding, and writer terminal-state handling.
2. **Repair capture truthfulness and history consistency:** clear ordering, partial
   response enrichment, Ollama folding, SSE termination, initial fetch/lost-event
   recovery, and deleted-detail identity.
3. **Finish Phase 4 usability and validation:** bounded facets, collision-safe config
   editing, null validation, restart reporting, malformed-render fallback, image
   preview, and failure-safe config persistence. Stabilize the stats fixture without
   weakening it.
4. **Agree D01–D05 and correct the affected documents in place.** In particular, decide
   raw storage, metric meaning, browser-origin protections, and in-flight filter
   semantics before implementing choices that alter those contracts.
5. **Re-run the actual gate:** stable full backend suite; frontend build; focused UI
   regressions for the failures above; clean publish plus executable/asset smoke; 10k-row
   UI exercise with high facet cardinality; the truncated-response lookup by warnings
   and by text in under ten seconds; real multi-turn tool/thinking traffic; live config
   editing/routing; clear-before; and reconnect with requests completing during the gap.

The Phase 4 spec permits manual frontend checks and did not require a frontend test
harness. Its absence is therefore not itself a scope violation. However, the reproduced
focus, rendering, cache, and layout failures justify a small targeted harness or
repeatable browser checks now. Prefer regression tests for these behaviours over a
large new snapshot suite. Keep all existing failing tests in place and leave every
implementation change uncommitted for review.
