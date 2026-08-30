# Phase 2 — Format Adapters: Implementation Spec

> Expands Phase 2 of [plan.md](../plan.md). Design authority is [architecture.md](../architecture.md);
> this spec makes the concrete decisions that document deliberately left open.
>
> **Goal:** rows have model, token counts, tok/s, stop reason, reassembled responses,
> and flattened searchable text — not just bytes. Rows from Ollama-native, Ollama `/v1`,
> LM Studio, and a live Anthropic call all show correct values; a deliberately garbage
> request still proxies and lands as `raw`.

## 0. Scope

**In:** format detection + three adapters (OpenAI chat, Anthropic messages, Ollama
native) running in the background writer (§5); SSE/NDJSON stream reassembly into
`response_body` (§4.3); normalized field extraction (model, tokens incl. cache
read/write, stop reason) (§5.1); token estimation + `tokens_estimated` (§5.4); warning
flags (§5.3); `tok_per_sec`; FTS population (see scope adjustment below); opt-in
`injectStreamUsage` (§3.4); golden-file fixtures; two small carry-ins from the Phase 1
review (§1 carry-ins).

**Out (explicitly):** UI and search *queries* (Phase 3/4 — this phase only writes FTS
rows), sessions semantics, SSE lifecycle events, replay, cost estimates, cold-load
badge polish (Phase 5 — but `load_duration` is extracted and stored in `warnings`-adjacent
data now, see D6), tool-call *rendering* (Phase 4 — this phase preserves structure only).

**Scope adjustment vs earlier notes:** phase-1.md D11 said "Phase 4 owns FTS
consistency". Reassigned: **Phase 2 owns FTS population and delete maintenance**,
because inserts must start the moment flattened text exists, and retention deletes must
keep the FTS table consistent from that same moment. Phase 4 wires the search *UI* only.
plan.md updated accordingly.

**No schema migration is needed** — schema v1 already has every column this phase
populates (`format`, `model`, `tok_per_sec`, `tokens_*`, `stop_reason`, `warnings`,
`response_body`+`response_raw`) and `requests_fts` already exists.

---

## 1. Key implementation decisions

Deviations found during implementation go back into this file and, if architectural,
into architecture.md §12.

### Carry-ins from the Phase 1 code review

- **Writer resilience (fix first):** `CaptureWriterService.RunAsync` wraps the whole
  loop in one try/catch — a single `InsertBatch` exception (transient `SQLITE_BUSY`
  from a user's DB browser holding a write lock, momentary disk-full) kills capture for
  the remaining process lifetime. Move the catch inside the loop: a failing batch is
  logged (Warning) and **dropped**, and the loop continues; escalate to the current
  give-up-loudly behavior only after N consecutive failures (N = 5). A test covers
  "store throws once → later batches still land."
- **`LastResponseByteMs` mark:** `tok_per_sec` needs "last response byte" (§4.2:
  output tokens ÷ (last byte − first byte)). Stamp it on every response-tee write
  (overwrite semantics, mirroring `MarkRequestForwarded`); `duration_ms` is close but
  includes handler epilogue. Carried on `CaptureRecord`.

### D1 — Enrichment runs in the writer, between dequeue and insert

`FormatEnricher.Enrich(CaptureRecord) → EnrichedRecord` is a pure function called by
`CaptureWriterService` on the writer thread, per record, before `InsertBatch`.
`EnrichedRecord` wraps the original record plus: `Format`, `Model`, `TokPerSec`,
`TokensIn/Out/CachedRead/CachedWrite`, `TokensEstimated`, `StopReason`, `WarningsJson`,
`ReassembledResponse` (bytes or null), `PromptText`, `ResponseText`.

**An adapter failure never loses a row.** Any exception inside detection or an adapter
is caught by the enricher: the row falls back to `format = raw` with warning
`parse_error`, raw bytes intact, logged at Debug. Adapters are written not to throw on
malformed input (truncated streams are *expected* input, D4); the catch is a backstop.

### D2 — Detection: path first, payload shape second, backend type as tiebreak

Matching on the stored `path` (prefixes already stripped), suffix match:

| Path ends with | Format |
|---|---|
| `/api/chat` | `ollama-chat` |
| `/api/generate` | `ollama-generate` |
| `/chat/completions` | `openai-chat` |
| `/messages` | `anthropic-messages` |

Path miss → payload sniff on the (decoded, D3) request body JSON: `messages` +
`max_tokens` + top-level `system`-style shape with a response carrying `stop_reason`
→ anthropic; `messages` with a response carrying `choices` → openai; request `model` +
response NDJSON `done` field → ollama. Backend `type` breaks remaining ties (e.g. an
openai-compat server mounted at a nonstandard path). Nothing matches → `raw`, silently
(no warning — unknown traffic is normal, per the graceful-degradation principle).

**Accepted scope (post-Phase-4 addition, code review E2).** A fourth adapter,
`openai-responses`, was added for OpenAI's Responses API (`/v1/responses`) —
structurally distinct from OpenAI chat completions: request `input` (a string or an
array of message/tool items) instead of `messages`; response `output[]` (typed items —
`message`, `reasoning`, `function_call`, `function_call_output`, and others rendered as
raw JSON rather than dropped) instead of `choices`. Detection: path suffix
`/v1/responses` first, then payload sniff (request `input` + response `output[]`, no
`choices`), same tiebreak rules as the other three. Field mapping: `tokens_in`/`out` from
`usage.input_tokens`/`usage.output_tokens`; `tokens_cached_read` from
`usage.input_tokens_details.cached_tokens` (no cache-write equivalent on this API);
`stop_reason` normalizes `status`/`incomplete_details.reason` onto the same vocabulary
(`length`/`max_tokens` → truncated warning; `content_filter`/`refusal`/`error` → danger)
the other three adapters already use, rather than a fifth vocabulary the UI would need
special-casing for. Streaming reassembly is simpler than chat completions here: the
terminal SSE event (`response.completed`/`.incomplete`/`.failed`) already carries the
complete final response object in its own `response` field, so reassembly is picking
that event out rather than folding deltas. Golden fixtures under
`Fixtures/openai-responses/`, same shape as the other three formats (D12). See also
architecture.md §5's adapter table.

Detection also runs for **error/failed rows** using the request side alone: a 502 to
`/api/chat` still gets `format = ollama-chat`, `model`, and `prompt_text` from the
request body — failed requests must be as browsable as successful ones. Response-side
fields stay null.

### D3 — Bodies are decoded for parsing only; stored bytes stay wire

If `Content-Encoding` is present on either side: decode gzip/deflate/br
(`System.IO.Compression`) or zstd (ZstdSharp) **into a scratch buffer for parsing**.
Stored `request_body`/`response_raw` remain the wire bytes (Phase 1 D5 unchanged).
The *reassembled* `response_body` is always stored unencoded (it's a Vessel-synthesized
document, not wire bytes). Unknown encoding → `raw` + `parse_error`. A `truncated`
capture (hit the `maxBodyMb` cap) is parsed as far as it goes, like a truncated stream.

**Finding (code review R05 + decision D01, resolved).** Two corrections landed here:

- *Wire-true storage was violated and is restored.* A post-Phase-4 change made enrichment
  write the **decoded** bytes back into `response_body` for non-streamed rows, so the
  detail pane would show JSON instead of base64 for a gzip/br backend response — trading
  the fidelity this decision exists to protect for display convenience, with a test that
  enshrined it. Storage is wire-true again; the display decode moved to **read time** in
  the detail endpoint (`SqliteReadStore.ToBodyPayload`), which is where it belonged. The
  test was corrected rather than deleted, and now asserts both halves: the stored bytes
  are still gzip, and the row still parses.
- *Decoding now has an output budget.* The `maxBodyMb` cap bounds **compressed** wire
  bytes, which says nothing about how far they expand — a 2 KB gzip body decoded to 2 MB
  with the row marked untruncated, and stacked encodings compound it. `BodyDecoder` takes
  a decoded-byte budget (**the same `capture.maxBodyMb`** — one number for "how much of
  one request Vessel holds in memory", `CaptureBudget`), enforced *during* a streaming
  copy so nothing larger is ever allocated, across every codec and every layer of a
  stacked encoding. The outcome is explicit — `Decoded | TruncatedDecode | Failed`. A
  truncated decode is treated exactly like a truncated capture per the paragraph above:
  parsed as far as it goes, flagged `body_truncated` on the row, and flagged
  `decodeTruncated` on the body in `GET /requests/{id}` so the UI never presents a prefix
  as a whole document.

### D4 — Stream parsing: one SSE parser, one NDJSON splitter, truncation-tolerant

- **SSE parser** (`SseParser`): operates on the complete captured buffer. Handles LF
  and CRLF, multi-`data:`-line events, comment/keep-alive lines (`:` prefix), the
  OpenAI `data: [DONE]` sentinel, and Anthropic `event:` types. A final event cut off
  mid-bytes is discarded; the warning `stream_incomplete` is set iff the stream lacks
  its terminal marker (`[DONE]` / `message_stop` / OpenAI final chunk with
  `finish_reason`) — which also covers client-disconnect and `maxBodyMb`-truncated
  captures. Partial content still yields `response_text` and the reassembled body.
- **NDJSON**: split on newlines, parse each line, skip an unparseable final fragment,
  `stream_incomplete` iff no `done: true` object arrived.

### D5 — Reassembly synthesizes the non-streamed document shape

For streamed responses, `response_body` gets a Vessel-synthesized JSON document in the
provider's own *non-streamed* response shape (an OpenAI `chat.completion` object, an
Anthropic `message`, an Ollama final object with the full `message`), so Phase 3+ UI
renders streamed and non-streamed identically:

- **OpenAI**: concatenate `choices[*].delta.content` (and `reasoning_content` where
  present) per choice index; fold `delta.tool_calls` by `index`, concatenating
  `function.arguments` fragments; take the last non-null `finish_reason`; `usage` from
  the final usage-bearing chunk; `model`/`id` from the first chunk.
- **Anthropic**: `message_start` seeds the message (model, role, `usage.input_tokens`,
  cache tokens); `content_block_start`/`content_block_delta` build blocks —
  `text_delta` appends text, `input_json_delta` concatenates tool-use input JSON,
  `thinking_delta` appends thinking; `message_delta` supplies `stop_reason` and
  `usage.output_tokens`.
- **Ollama**: concatenate `message.content` (chat) / `response` (generate); the
  `done: true` object supplies everything else and its non-content fields are merged
  into the synthesized document (so `eval_count`, durations etc. are visible in the
  stored body too).

Tool-call structure is preserved *as the provider represents it* inside the synthesized
document — no cross-provider normalization of tool calls this phase (Phase 4 renders
per-format).

### D6 — Field extraction

| Field | openai-chat | anthropic-messages | ollama-chat / -generate |
|---|---|---|---|
| `model` | response `model`, else request `model` | same | same |
| `tokens_in` | `usage.prompt_tokens` | `usage.input_tokens` (+ both cache token counts — Anthropic reports them disjointly; the UI wants total submitted context) | `prompt_eval_count` |
| `tokens_out` | `usage.completion_tokens` | `usage.output_tokens` | `eval_count` |
| `tokens_cached_read` | `usage.prompt_tokens_details.cached_tokens` | `usage.cache_read_input_tokens` | — |
| `tokens_cached_write` | — | `usage.cache_creation_input_tokens` | — |
| `stop_reason` | `finish_reason` (`stop`, `length`, `tool_calls`, …) | `stop_reason` (`end_turn`, `max_tokens`, `tool_use`, …) | `done_reason` (`stop`, `length`, `load`, …) |
| `tok_per_sec` | see below | see below | **exact**: `eval_count / eval_duration` (ns→s) |

`stop_reason` stores the provider's literal string — no normalization; the
truncation *warning* (D7) is where cross-provider meaning lives.

`tok_per_sec` (non-Ollama): `tokens_out / ((LastResponseByteMs − FirstResponseByteMs)/1000)`,
computed only when streamed and the denominator ≥ 100 ms (a non-streamed body arrives
in one burst — the wire timing measures network, not generation; leave null).
Ollama-native rows use the exact eval numbers for both streamed and non-streamed.
Additionally for Ollama, `load_duration` > 1 s is surfaced now as warning `cold_load`
(the data is in hand; Phase 5 only polishes presentation/threshold).

**Finding (code review D02, resolved).** A post-Phase-4 change made non-streamed
non-Ollama rows fall back to `tokens_out / duration_ms` instead of null, and tests
required that fallback. Total request duration folds in queueing, prefill, and network
transfer time — it is not the same quantity as generation throughput, and reporting it
under `tok_per_sec` would silently pollute session averages with a different metric.
Restored: non-streamed non-Ollama rows report `tok_per_sec = null` unconditionally, as
this section originally specified. The row still shows duration and token counts, so no
information is lost — only the mislabeled rate. See also architecture.md §4.2.

### D7 — Warning vocabulary (JSON array of string codes, `warnings` column)

`truncated_response` (stop reason is `length`/`max_tokens`), `http_error` (status ≥ 400),
`proxy_error` (row has `error` set), `client_disconnect`, `tokens_estimated`,
`stream_incomplete`, `parse_error`, `body_truncated` (capture cap hit), `cold_load`,
`slow_ttft` (`ttft_ms` > `warnings.slowTtftMs`; config default **5000**, `0` disables;
suppressed when `cold_load` explains it). Codes are constants in one class; the UI maps
codes to badges later. Empty array → NULL column.

### D8 — Token estimation

When a chat-format row lacks reported usage (OpenAI-format stream without
`include_usage` being the canonical case): `tokens_out` ≈ ceil(`response_text`.length/4),
`tokens_in` ≈ ceil(flattened prompt length/4), `tokens_estimated = 1` + warning.
Estimation only fills **missing** values — never overwrites a reported count
(estimated-in + reported-out is possible and fine: each estimated value flags the row).

### D9 — Text flattening (`prompt_text` / `response_text`)

Purpose-built for FTS and list preview, not display. `prompt_text`: system prompt +
each message's text content, `role: text` lines, newline-joined. Content blocks:
text blocks verbatim; tool definitions skipped; tool-use/tool-result blocks contribute
tool name + stringified args/result; image blocks contribute nothing (base64 never
enters FTS). `response_text`: assistant text (+ tool-call names/args), thinking text
included. Raw rows: null — FTS only ever holds parsed-format rows.

### D10 — FTS population and consistency (owned here from now on)

`InsertBatch` inserts `(rowid = requests.id, prompt_text, response_text)` into
`requests_fts` for rows where either text is non-null, in the same transaction as the
row. Both retention paths delete FTS rows for the ids they remove, same transaction
(contentless FTS supports `DELETE ... WHERE rowid IN (…)` via `contentless_delete=1`).
"Clear all/before" (Phase 4) inherits the same store helper.

### D11 — `injectStreamUsage`: the one request-path feature

Per-backend, default **off** (architecture §3.4). In `ProxyHandler`, before
`SendAsync`, when: the backend has `injectStreamUsage: true` **and** the path ends in
`/chat/completions` **and** the request has no `Content-Encoding` **and** the body
parses as a JSON object with `"stream": true` **and** `stream_options` is absent —
buffer the body (it's already destined for the capture buffer; size-capped by
`maxBodyMb`, over-cap requests are forwarded unmodified), add
`"stream_options": {"include_usage": true}`, forward the modified bytes with a
corrected `Content-Length`.

Capture stores the **original client bytes** (the tee wraps the original stream before
modification) plus a `usage_injected` marker in `warnings` — the stored record must
never misrepresent what the client sent, and the marker explains why the response
contains a usage chunk the client didn't ask for. Any parse failure → forward
unmodified, no warning. Config validation: `injectStreamUsage` only meaningful on
`type: openai` backends (warn at startup otherwise, don't fail).

### D12 — Golden-file fixtures, recorded through Vessel itself

`tests/Vessel.Tests/Fixtures/{format}/{case}/` with up to four files per case:
`request.json` (wire request body), `response.raw` (exact wire response bytes — SSE/
NDJSON chunk stream or non-streamed JSON), `meta.json` (path, status, content-type,
content-encoding), `expected.json` (every D6 field + warnings + `prompt_text`/
`response_text` + the synthesized `response_body` for streamed cases).

Recording: `verify/record-fixtures.ps1` drives real traffic through Vessel and exports
`request_body`/`response_raw` from `vessel.db` (decompressed) — Vessel records its own
fixtures, so fixture bytes are wire-true by construction. Malformed/truncated cases are
derived by hand from real ones (cut mid-event, cut mid-UTF-8-codepoint, garbage line
injected). Minimum case set: per format — streamed + non-streamed happy path, tool-call
response, `max_tokens`-truncated stop, mid-stream cut; OpenAI — with and without usage
chunk; Anthropic — cache-token case, thinking case; Ollama — generate (not just chat),
cold-load case (`load_duration` large); one gzip-encoded non-streamed case; plus the
existing garbage/raw case. Live OpenAI/Anthropic recordings gated behind opt-in flags
like `verify.ps1` (LM Studio case optional, recorded if a server is running).

### Deviations found during implementation

- **D7 refinement — `http_error` and `proxy_error` are mutually exclusive.** D7 lists
  them as independent triggers (`http_error` = status ≥ 400; `proxy_error` = row has
  `error` set), but a Vessel-synthesized proxy failure (unknown_backend/unreachable/
  timeout) always carries a 4xx/5xx status too, so the literal rule tags every proxy
  failure with both codes. Implemented: `client_disconnect` when `error` is the
  client-disconnect code, else `proxy_error` when `error` is set, else `http_error` when
  status ≥ 400. This keeps each code meaning one thing — `http_error` = the *backend*
  returned a non-2xx; `proxy_error` = Vessel never got a response — and lets one fixture
  land exactly one code (F7).
- **D8 refinement — estimation is gated to non-error responses.** Estimation fills a
  missing count only when there is a real response with status < 400. A backend 4xx/5xx
  isn't a normal generation, so estimating tokens from the request prompt there is noise
  and would muddy the `http_error` row; error rows stay exact-or-null.
- **F4 needed a request-body capture fix.** For a genuinely dead backend (connection
  refused) YARP never reads the request body, so a naive read-through tee captured
  nothing and the error row had no `model`/`prompt_text`. `ProxyHandler` now drains any
  unread request body in its `finally` (bounded by the capture cap — request bodies are
  single JSON documents), so failed rows enrich from the request side as D2/F4 require.
- **`ICaptureStore` seam.** Extracted a tiny interface over `SqliteCaptureStore` so the
  writer's resilience (F10) can be tested against a store that throws on demand.
- **Fixtures hand-authored, not live-recorded.** No Ollama/live API keys were available
  at authoring time, so the golden fixtures are hand-authored to the documented wire
  shape rather than recorded through Vessel. `verify/record-fixtures.ps1` re-records them
  wire-true against a real Ollama (and `.gitattributes` marks the fixture tree binary so
  byte-exact SSE newlines and the gzip case survive checkout).

---

## 2. New/changed layout

```
src/Vessel/
  Formats/
    FormatEnricher.cs        # D1 entry point + the exception backstop
    EnrichedRecord.cs
    FormatDetector.cs        # D2
    SseParser.cs             # D4 (shared: OpenAI + Anthropic)
    NdjsonParser.cs          # D4
    OpenAiChatAdapter.cs     # D5/D6
    AnthropicMessagesAdapter.cs
    OllamaAdapter.cs         # chat + generate
    TextFlattener.cs         # D9
    TokenEstimator.cs        # D8
    Warnings.cs              # D7 constants
    BodyDecoder.cs           # D3 content-encoding handling
tests/Vessel.Tests/
  Fixtures/...               # D12
  FormatDetectorTests.cs
  SseParserTests.cs
  AdapterGoldenTests.cs      # one parameterized suite over the fixture tree
  EnricherIntegrationTests.cs# end-to-end through proxy + writer + DB
  InjectStreamUsageTests.cs
verify/
  record-fixtures.ps1        # D12 recorder
```

Changed: `CaptureWriterService` (resilience + enrich call), `SqliteCaptureStore`
(insert gains enrichment columns + FTS; retention gains FTS deletes),
`CaptureContext`/`ResponseTeeStream` (`LastResponseByteMs`), `ProxyHandler` (D11),
`CaptureRecord` (carries last-byte mark), `VesselConfig` (`injectStreamUsage`,
`warnings.slowTtftMs`).

---

## 3. Automated tests

| # | Assertion |
|---|---|
| F1 | **Golden suite**: every fixture case → exact match on all `expected.json` fields, including synthesized `response_body` for streamed cases |
| F2 | **SSE parser units**: LF + CRLF, multi-line `data:`, keep-alive comments, `[DONE]`, event cut mid-byte → prior events intact, no terminal marker → `stream_incomplete` |
| F3 | **Detection**: each path suffix; payload sniff with prefix-less paths; backend-type tiebreak; nothing matches → `raw` with **no** warning |
| F4 | **Error rows enrich from the request side**: dead backend + `/api/chat` body → `format = ollama-chat`, `model` set, `prompt_text` set, response fields null |
| F5 | **Estimation**: usage-less OpenAI stream → both counts estimated + flagged; reported values never overwritten; mixed reported/estimated flags the row |
| F6 | **tok/s**: Ollama fixture → exact `eval_count/eval_duration` value; streamed OpenAI → wire-timing value; non-streamed non-Ollama → NULL |
| F7 | **Warnings**: one fixture per code lands exactly that code (incl. `cold_load` suppressing `slow_ttft`) |
| F8 | **FTS**: parsed rows searchable via direct `requests_fts MATCH` query; raw rows absent; retention (both caps) leaves zero orphaned FTS rows |
| F9 | **injectStreamUsage on**: stub receives modified body + corrected `Content-Length`; DB stores original client bytes + `usage_injected`; **off** (default): bytes untouched. Skip conditions (already has `stream_options`, non-JSON, `Content-Encoding`, over-cap) each forward unmodified |
| F10 | **Writer resilience**: store throws on one batch → batch dropped + logged, subsequent batches land; 5 consecutive failures → loop gives up loudly (existing behavior) |
| F11 | **Enricher backstop**: adapter forced to throw → row lands as `raw` + `parse_error`, bytes intact |
| F12 | Phase 0 T7 / Phase 1 C2 still green — enrichment is writer-side and must not show up in chunk-arrival timing |

---

## 4. Acceptance criteria (phase gate)

1. All §3 tests green; full prior suite still green (`dotnet test`).
2. `verify.ps1` extended: after each real-Ollama case it asserts the captured row's
   `format`, `model`, `tokens_in/out` (exact, from Ollama's counts), `tok_per_sec`
   non-null, `stop_reason` non-null; SSE case additionally checks the synthesized
   `response_body` parses and its text equals the direct call's text.
3. Fixture tree committed with real recorded bytes for all three formats (live
   OpenAI/Anthropic cases recorded if keys were available; committed fixtures
   redact/replace any real key material — recorded from *response* side only,
   `request.json` fixtures use dummy keys).
4. A hand-browse of `vessel.db` after a real session shows enriched rows and
   `requests_fts MATCH` finds a known phrase.
5. Publish smoke still passes (no new packages expected; `System.IO.Compression` is in-box).
6. plan.md Phase 2 boxes ticked; deviations recorded here; architecture.md §12 updated
   if any decision here proves wrong.
