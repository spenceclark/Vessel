# Phase 5 — Replay & Compare: Implementation Spec

> Expands Phase 5 of [plan.md](../plan.md). Design authority: [architecture.md](../architecture.md)
> (backend) and [ui-spec.md](../ui-spec.md) (frontend).
>
> **Goal:** "would qwen handle what opus handled?" answered without touching client
> code — replay any captured request against a compatible backend/model, compare the
> replay with its original side by side, and export any capture as curl.

## 0. Scope

**In:** replay endpoint + server-side execution through the normal proxy pipeline;
auth re-attachment from environment variables; model override; `replay_of` linking,
end to end (column → events → UI); the **Compare** view for replay pairs; copy-as-curl.

**Out (explicitly, per pre-agreed decisions):** cross-format wire translation
(Phase 8); prompt editing before re-send (replay is *re-send*, not a playground);
batch replay / eval scoring; **arbitrary two-row diff — Compare renders a
`replay_of` pair only**; replay via MCP (Phase 5b is read-only).

**No schema migration** — `replay_of` has existed since schema v1.

---

## 1. Key implementation decisions

### D1 — Replay executes as a real proxied request through Vessel itself

`POST /vessel/api/requests/{id}/replay` body `{ backend?: string, model?: string }`
(defaults: original backend, original model). The server builds an **internal HTTP
request to its own proxy endpoint** (`/b/{backend}{originalForwardPath}`) — so the
replay flows through the full pipeline and gets capture, tee timing, enrichment,
SSE lifecycle events, and UI liveness for free. No second execution path exists.

- **Body:** the stored request body, storage-decompressed **and content-decoded**
  (a replay is a new Vessel-originated request, not a wire re-transmission — it is
  always sent unencoded, no `Content-Encoding`). Model override: parse the decoded
  JSON, set the top-level `model` field (all four supported formats use it),
  re-serialize. Override on an unparseable body → 400.
- **Headers are minimal, not a faithful replay** — stored header values are redacted
  stubs and must never be sent. The replay sends: `Content-Type` from the original,
  `Accept` if the original had it, the `X-Vessel-*` control headers below, and auth
  per D2. Nothing else.
- **`X-Vessel-Replay-Of: {id}`** is a new control-plane header: parsed into
  `CaptureRecord.ReplayOf` at capture, stripped before forwarding like all
  `X-Vessel-*`. It is what stamps the column — which also means a curl user can mark
  replays by hand; document as intended.
- **Tags:** the replay carries the original row's tags (`X-Vessel-Tags`), so filters
  group originals with their replays. Session: current session at replay time.
- **Async contract:** the endpoint validates everything it can up front (unknown id →
  404; unknown backend → 400; format incompatibility per D3 → 400 `format_mismatch`;
  missing auth env var → 400 naming the variable), then fires the internal request on
  a background task that **drains the response** (capture needs the body to complete;
  the drain respects the same activity timeout as any proxied request) and returns
  **202 `{}`** immediately. No id/seq in the response — correlation is D4.
  *(Amended by #48: the body is now `{ replayGroup, count }` — the client needs the fan
  id to open the grid and to correlate `started` events. Still no child ids.)*

### D2 — Auth re-attachment (pre-agreed in plan.md, restated as the contract)

Never stored, never persisted. Local no-auth backends: auth omitted. Otherwise, from
**environment variables on the Vessel process**: `OPENAI_API_KEY` for openai-type,
`ANTHROPIC_API_KEY` for anthropic-type, overridable per backend via a new optional
`authEnv` config field (key-requiring OpenAI-compat backends, e.g. Unsloth Desktop).
Header shape follows the **target backend type**: `Authorization: Bearer …` for
openai-type; **`x-api-key: …` + `anthropic-version`** (copied from the original
request if present, else the current stable) for anthropic-type. Missing variable →
400 whose message names the exact variable; no paste-and-store fallback. The
replayed capture stores redacted headers like any other row — replays are
credential-free in the DB with no special-casing.

### D3 — Format compatibility matrix (no translation, ever, this phase)

Replay targets must speak the captured wire format at the original forward path:

| Row format | Allowed target backend types |
|---|---|
| `openai-chat` | `openai`, `ollama` (both serve `/v1/chat/completions`) |
| `openai-responses` | `openai` |
| `anthropic-messages` | `anthropic`, `ollama` (Anthropic-compat endpoint) |
| `ollama-chat` / `ollama-generate` | `ollama` |
| `raw` | the original backend only, no model override (bytes re-sent as captured) |

The replay dialog only offers allowed targets; the endpoint enforces the same matrix
(`format_mismatch` otherwise). `auto`-type backends are treated by their sniffed
row-format compatibility, conservatively.

### D4 — Correlation via `replayOf` on lifecycle events, not response plumbing

`started` events and the active-descriptor snapshot gain `replayOf?: number`
(capture knows it at registration from the header — immutable, so it joins the K0b
descriptor). `Summary.replayOf` already exists for `completed`/REST. UI flow: fire
replay → the new row appears as a normal in-flight row within milliseconds; the UI
watches for `replayOf === {id}` to badge/auto-highlight it. Multiple concurrent
replays of one original all correlate naturally. Contract change recorded in
phase-3.md D5 when landing.

### D5 — Compare view: a `replay_of` pair, nothing else

*(Amended by #48: Compare is a replay **group** — an original plus the members of one fan.
A pair is the one-member rendering. Amended again by #49: every column, the pair's included,
now carries a header with a 1-5 score control, so the pair render is no longer byte-identical
to phase 5's — deliberately.)*

Entry points: a replayed row's detail shows "Replay of #N — compare"; an original
with replays shows "Replays (n)" linking each. Compare takes over the detail-pane
region (own header: `#N → #M`, backends, models; close returns to detail):

- **Metrics strip** across the top: duration, TTFT, tok/s, tokens in/out, stop
  reason — original vs replay with per-metric delta rendering (faster/slower,
  fewer/more; neutral colors per ui-spec §2.2 — deltas are data, not status).
- **Request panel**: renders once (bodies are near-identical by construction) with a
  **param-diff list** on top: any differing top-level request params (model, and
  e.g. `temperature` if the formats differ in defaults) shown as `name: a → b`.
- **Response panels**: the two rendered responses side by side (existing MessageView,
  same renderers). **No inline word-diff in v1** — two sampled generations differ
  everywhere by nature; a textual diff renders noise as signal. Side-by-side reading
  plus the metrics strip is the honest comparison. (Revisit only if real use begs.)
- ui-spec.md gains a §5.3 Compare section (layout + delta rendering rules) in the
  same change — per its own §8.9 rule.

### D6 — Copy as curl: client-side, targets Vessel

Generated in the browser from the detail payload (no new endpoint): method, URL
through Vessel (`http://{listen}/b/{backend}{path}` — so running the curl is *also
captured*), `Content-Type`, decoded body (single-quoted heredoc-safe escaping), and
for auth-requiring backends a **placeholder** (`-H "Authorization: Bearer
$OPENAI_API_KEY"` / `-H "x-api-key: $ANTHROPIC_API_KEY"`) — never a value, matching
D2's env-var convention. Base64 bodies → curl `--data-binary` with a note comment.
Copy button on the detail Request tab and the row context.

---

## 2. New/changed layout

```
src/Vessel/
  Api/ReplayEndpoint.cs          # D1 validate + fire; D2 auth; D3 matrix
  Api/ReplayExecutor.cs          # internal self-request + drain (background)
  (RouteResolver/ProxyHandler)   # X-Vessel-Replay-Of parse → CaptureRecord.ReplayOf
  (CaptureEvents)                # D4: replayOf on started + descriptor
  (Config)                       # BackendConfig.AuthEnv
frontend/src/
  components/{ReplayDialog,CompareView,MetricsDelta}.tsx
  lib/curl.ts                    # D6
  (DetailPane, RequestRow)       # entry points, replay badge
tests/Vessel.Tests/ReplayTests.cs
frontend: replay/compare/curl component tests
```

## 3. Automated tests

| # | Assertion |
|---|---|
| P1 | Validation: unknown id 404; unknown backend, `format_mismatch` per full D3 matrix, missing env var (message names it) → 400; nothing fired on any 400 |
| P2 | Happy path: replay of an `ollama-chat` row → stub receives decoded body, no stale headers, no redacted values; row lands with `replay_of` set, original tags, current session |
| P3 | Model override rewrites only `model`; original stored row unchanged; unparseable body + override → 400 |
| P4 | Auth: openai-type target gets `Authorization: Bearer` from the configured env var (incl. `authEnv` override); anthropic-type gets `x-api-key` + `anthropic-version`, **not** Bearer; replayed row's stored headers are redacted |
| P5 | Events: `started` carries `replayOf`; descriptor snapshot carries it (frame-loss recovery keeps the badge); `completed.row.replayOf` correct |
| P6 | Streamed replay: internal drain completes capture; enrichment/tok-s present; concurrent replays of one original both land and correlate |
| P7 | curl: snapshot tests per format incl. auth placeholder, base64 body, quote-hostile content; generated command round-trips against a stub |
| P8 | Compare UI: renders pair, metric deltas correct sign, param diff shows model change; raw rows offer no compare-with-override path |

## 4. Manual gate

1. Replay a real Ollama agent request against a second local backend (LM Studio or a
   second Ollama model) via model override; watch it appear live; Compare reads well.
2. One live-API replay with the env var set; one without (error names the variable).
3. Copy-as-curl of a streamed capture; run it; the run is itself captured.
4. plan.md Phase 5 ticked; ui-spec §5.3 added; phase-3.md D5 updated (D4).

## 5. Acceptance

Suites green (backend + frontend); manual gate done; the plan's "done when" — answer
"would qwen handle what opus handled?" without touching client code — demonstrated
end to end on real traffic.

---

## 6. Post-implementation remediation (from phase-5-report.md — acceptance blocked)

### 6.1 Decisions

> **Status: PD1–PD4 ALL APPROVED as recommended, 2026-08-29.** Implementing agents
> treat the recommendation column as authoritative; R11's doc pass records the
> outcomes in their owning documents.

| # | Decision | Recommendation |
|---|---|---|
| PD1 | Default config: nine backends or one? (report deviation 2) | **Revert to Ollama-only first-run default.** Zero-config drop-in is a core principle, Phase 6's first-run item promises exactly that, and eight unused backends make a stranger's first popover a wall of "unknown". Existing user configs are untouched (vessel.json is never overwritten); the README documents adding backends. |
| PD2 | Local anthropic-type backends and replay auth (finding 8) | **Align with the openai loopback rule**: loopback anthropic-type targets omit auth. Ollama's Anthropic-compat endpoint is precisely the local anthropic-type use case, and D2's "local no-auth backends replay with auth omitted" already says this — the implementation is the outlier, not the spec. |
| PD3 | Dialog backdrop-click-to-close removal (report deviation 4) | **Keep the new behavior** (Escape + explicit close only — safer for typed-confirmation dialogs, and now test-pinned) and correct ui-spec's older narrative in place so the doc carries one account. |
| PD4 | CI on windows-latest only (report deviation 3) | **Restore Linux**: matrix `ubuntu-latest` + `windows-latest` for the backend job. If ubuntu was dropped because it failed, the failure is a Phase 6 blocker to diagnose now, not a runner to delete — linux-x64 is a shipping RID. |

### 6.2 Fixes (correctness findings 1–9, then coverage)

- [x] R1 — `shellQuote` emits the correct `'"'"'` sequence; test pins the exact output
  and covers URL/method/content-type paths (the prior test was vacuous — heredoc path
  only).
- [x] R2 — curl auth placeholder uses the same rule as `ReplayEndpoint.TryBuildAuth`:
  `authEnv` name when set (`StatusBackend` gains `authEnv` — a variable *name*, never a
  secret), no auth for loopback openai/auto, anthropic shape for anthropic-type. D2
  and D6 are one rule; extract it if needed.
- [x] R3 — Compare renders the request once with the param diff on top, per D5/§5.3.
- [x] R4 — `MetricDelta` formats `Math.abs(delta)` and prepends the sign (Δ −1.50s,
  not −1500ms).
- [x] R5 — Replay rejects capture-truncated bodies (`Summary.Truncated`) and
  decode-failed still-encoded bodies with clear 400s; never sends partial or
  mis-encoded bytes.
- [x] R6 — `ReplayDialog` backend state reconciles against `allowed`
  (`allowed.find(...) ?? allowed[0]`); blank model override treated as no override.
- [x] R7 — PD2 implemented (loopback anthropic omits auth).
- [x] R8 — F9: rows keep the precise `ForwarderError` identity; `BackendHealthTracker`
  classifies unreachability itself from the specific errors that mean it
  (`upstream_unreachable`, `upstream_timeout`, connect-class failures), not from the
  row's collapsed label. Health dot never flips red on client-side or mid-stream
  delivery errors.
- [x] R9 — Smaller items from the report: required (non-nullable) tracker dependency;
  drain timeout bounded explicitly; loopback-normalized executor target for
  `0.0.0.0`/`[::]` binds; a small cap on concurrent replay dispatches; single-snapshot
  config read in `ReplayEndpoint`; clipboard write wrapped with a visible failure
  toast; popover a11y (labelled `group`, drop bare `role="dialog"`); indent tidy.
- [x] R10 — Coverage to the §3 table: complete P1–P4/P7 (full D3 matrix incl.
  nothing-fired assertions, stub-side wire inspection via the already-built
  `ReflectPayload` fields, gzip-original decoded-body case, only-model/original-
  unchanged/unparseable-400, per-format curl snapshots + stub round-trip) and add
  P5, P6, P8 (started/descriptor `replayOf` incl. frame-loss recovery; streamed
  drain + enrichment + concurrent replays; CompareView/ReplayDialog component tests).
- [x] R11 — Doc truth pass: PD1/PD3/PD4 outcomes recorded (Phase 6 first-run item,
  ui-spec dialog narrative, CI note); report deviation 6's endpoint-table change
  confirmed in architecture.md; report updated to accepted when done.

**Acceptance:** spec §5 criteria all met, including the §3 table fully covered; report
flipped to accepted.

- [x] R12 — Backend `type` semantics surfaced (follow-on from report deviation 5,
  which is accepted as the correct reading): (a) the first-run default config
  creates the Ollama backend with `type: "ollama"`, **not** `auto` — `auto` means
  "unknown dialect" and correctly locks a backend out of cross-backend replay
  targeting, which is wrong for the one backend we *know*; (b) the config panel's
  type dropdown gains a one-line `xs --text-muted` explainer per ui-spec's
  settings-control rule: "auto = detect from traffic; observation only — typed
  backends unlock replay targeting and correct replay auth."
