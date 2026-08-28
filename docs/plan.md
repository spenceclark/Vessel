# Vessel — Implementation Plan

Phased so that **every phase ends with something you can run against your real Ollama
traffic**. Ordering principle: proxy correctness first (the part that can break your
workflow), capture second, UI third, features last. See [architecture.md](architecture.md)
for design detail; section references below (§) point there.

---

## Phase 0 — Transparent proxy skeleton

*Goal: Vessel can sit in front of Ollama all day without you noticing it exists.*

- [x] Solution + project layout per §11; .NET 10 web project, YARP package.
- [x] `vessel.json` config load/create-on-first-run (§9); backends model.
- [x] Routing: default backend, `/b/{name}/` path prefix, `X-Vessel-Backend` header;
      strip `X-Vessel-*` before forwarding (§3.2).
- [x] Streaming pass-through verified: SSE and NDJSON flow chunk-by-chunk, no buffering.
- [x] Remote HTTPS backends work (OpenAI/Anthropic as ordinary outbound TLS) — wired and
      forwarded as plain YARP outbound HTTPS; live-key verification pending (opt-in
      `verify.ps1 -OpenAI -Anthropic`).
- [x] `404` with helpful JSON on unknown backend / upstream connection failure
      (404 `unknown_backend`, 502 `upstream_unreachable`, 504 `upstream_timeout`).

**Done when:** `OLLAMA_HOST`-style clients, an OpenAI SDK with
`base_url=http://localhost:4550/b/openai/v1`, and `curl -N` streamed requests all behave
byte-identically with and without Vessel in the middle. Daily-drive it from here on.

---

## Phase 1 — Capture and persistence

*Goal: everything that passes through is recorded, cheaply.*

- [x] Tee streams for request/response bodies with memory cap + `truncated` flag (§4.1).
- [x] Timings: `duration_ms`, `ttft_ms`, `vessel_overhead_ms` on monotonic clock (§4.2).
- [x] Header redaction before the record leaves the request path (§8).
- [x] `Channel<CaptureRecord>` + background writer, batched transactions (§6.1).
- [x] SQLite schema + migrations, WAL, zstd body compression (§6.2).
- [x] Raw-fallback capture: unknown traffic stored with timing + bodies, silently (§5).
- [x] Retention: `maxRequests` / `maxDbSizeMb` enforcement + incremental vacuum (§6.4).
- [x] Tests: tee doesn't alter bytes; timings sane; redaction covers all auth headers.

**Done when:** a day of real traffic produces a `vessel.db` you can query by hand in a
SQLite browser, DB stays under the cap, and no plaintext key appears anywhere in it.

---

## Phase 2 — Format adapters

*Goal: rows have model, tokens, stop reason, flattened text — not just bytes.*

- [x] Adapter interface + payload sniffing (§5); runs in the writer, never request path.
- [x] **Ollama native** (`/api/chat`, `/api/generate`): NDJSON reassembly; exact
      `eval_count`/`eval_duration` metrics; `load_duration` captured (§5, §5.3).
- [x] **OpenAI chat**: SSE delta reassembly; usage-from-final-chunk;
      `chars/4` estimation + `tokens_estimated` flag when absent (§5.4); optional
      per-backend `injectStreamUsage` (§3.4).
- [x] **Anthropic messages**: event-type folding; usage from `message_start`/`message_delta`;
      cache read/write tokens (§5).
- [x] Warning flags: `length`/`max_tokens` stop, non-2xx, client disconnect, estimated
      tokens, high TTFT (§5.3).
- [x] Store reassembled + raw chunk stream for streamed responses (§4.3).
- [x] FTS population + delete maintenance (moved here from Phase 4 — text exists now,
      so consistency starts now; Phase 4 wires the search UI only).
- [x] Test fixtures: golden files for all three formats (streamed + non-streamed, tool
      calls, truncation, cold-load, cache tokens, thinking, gzip, plus malformed/truncated
      cases). Hand-authored to the documented wire shape (no live backend at authoring
      time); `verify/record-fixtures.ps1` re-records them wire-true against real Ollama.

**Done when:** rows from Ollama-native, Ollama `/v1`, LM Studio, and a live Anthropic call
all show correct model, token counts, tok/s, and stop reason; a deliberately garbage
request still proxies and lands as `raw`.

---

## Phase 3 — Minimal UI

*Goal: stop using the SQLite browser.*

- [x] Vite + React + TS + Tailwind + shadcn scaffold; embedded static hosting under
      `/vessel/`; publish pipeline runs `vite build` (§10, §11).
- [x] REST: list (paged, basic filters), detail; stats endpoint (§7).
- [x] SSE `/vessel/api/events` with lifecycle events; live in-flight rows with running
      timer (§4.4).
- [x] History list: virtualized, reverse-chron, row = path/model/duration/tok-s/tags/badge.
- [x] Detail tabs: Overview (metrics, warnings), Headers (redacted marked), Request,
      Response — raw JSON toggles first; rich message rendering next.
- [x] Sessions: marker rows, stats bar (total/failed, avg latency, avg tok/s, avg TTFT),
      Reset button (§6.3).

**Done when:** you keep a browser tab open on Vessel while working and it's genuinely
useful — live rows appear as agents run, clicking one answers "what did it send and what
came back".

---

## Phase 4 — Search, filters, rendering polish

*Goal: findability and readability at 10k requests.*

- [x] FTS5 index + free-text search wired into the list (§6.2).
- [x] Full filter bar: backend, model, tag, status, format, session, warnings-only (§7).
- [x] Tags end-to-end: header + `/t/` path capture → row display → filter (§3.3).
- [x] Rendered message view: system/user/assistant blocks, markdown, long-content collapse.
- [x] **Tool calls as cards** — name, args, result, collapsible (§5.2).
- [x] Rate-limit headers + cache token metrics on Overview (§10).
- [x] Clear-all / clear-before-date; config editor page (backends, retention) (§7, §9).

**Done when:** "find that request from this morning where the agent got truncated" takes
under ten seconds: filter warnings-only, or search a phrase, click, read.

---

## Phase 5 — Differentiator features

*Each independent — pick by mood. Suggested order:*

- [ ] **Replay** (§7): re-send with optional backend/model override; `replay_of` link both
      directions in the UI; **Copy as curl**. *The killer feature — do it first.*
      Decisions pre-agreed for the phase spec:
      - **Same wire format only in v1** — replay to any backend speaking the captured
        format (Ollama/LM Studio/llama.cpp/live all speak OpenAI format via `/v1`, so
        cross-backend comparison works broadly). Cross-provider replay needs
        **wire-format transformers** (ollama-native ⇄ openai-chat ⇄ anthropic-messages,
        incl. non-1:1 params like `num_predict` vs `max_tokens`) — explicitly a later
        item, noted here so it isn't rediscovered.
      - **Auth is never stored** (redaction stands; vessel.json/vessel.db stay
        credential-free). Local no-auth backends replay with auth omitted. Otherwise
        Vessel re-attaches from **environment variables on the Vessel process**, the
        standard names apps already look for — `OPENAI_API_KEY`, `ANTHROPIC_API_KEY` —
        with an optional per-backend `authEnv` config field naming the variable for
        key-requiring OpenAI-compat backends (e.g. Unsloth Desktop). Header shape
        follows backend type: `Authorization: Bearer` for openai-type,
        **`x-api-key` (+ `anthropic-version`) for anthropic-type — not Bearer**.
        Missing env var → replay dialog says which variable to set; no paste-and-store.
- [ ] **Diff**: pick two requests, side-by-side message/param diff. Pairs naturally with
      replay ("same prompt, two models").
- [ ] **Warning badges polish**: cold-load detection via Ollama `load_duration`,
      configurable TTFT threshold (§5.3).
- [ ] **Cost estimates**: static pricing table + `pricing` config overrides, `~$0.0042`
      on Overview and session totals; clearly labeled estimate (§9).
- [ ] **Context-growth chart**: `tokens_in` over time per session/tag — makes agent
      context bloat visible at a glance.
- [ ] **Live tail**: stream the in-flight response into the UI as it generates —
      the in-flight detail's state line (ui-spec §9.1) becomes a live token view.
      Needs its own design pass: chunk broadcast only while a client has that
      request open, bounded per-request buffers, drop-never-block — the hot-path
      rules from §4.1 apply in full.
- [ ] **Ollama panel**: `ollama ps` proxied view (loaded models, VRAM); server.log viewer
      if reachable (§7, §10).
- [ ] **Export to CSV/JSONL**: provide option for a range of requests to be exported to CSV/JSON (with/without bodies).
      Allows offline analysis by user/AI.
- [ ] **MCP server**: let the user's own AI tools (Claude Code, etc.) interrogate the
      captured traffic — "why did my planner agent stall?" answered by the agent
      querying Vessel directly. Streamable HTTP endpoint at `/vessel/mcp` on the
      existing host via the official ModelContextProtocol C# SDK (stdio bridge only
      if a client demands it). **Read-only v1**: `search_requests` (FTS + filters),
      `get_request`, `get_stats`, `list_sessions` — no replay/clear via MCP without
      a separate decision. The core design work is token-budget shaping: summaries
      and truncated bodies by default, explicit params to fetch more — never dump a
      200K-token context into the caller. Same trust boundary as the UI (localhost,
      D03 Host guard); docs must say an MCP client gets your captured prompts.

**Done when:** replay + diff let you answer "would qwen handle what opus handled?" without
touching client code.

---

## Phase 6 — Ship / open-source

- [ ] Self-contained single-file publish for win-x64, linux-x64, osx-arm64, osx-x64;
      trimming warnings resolved (§11).
- [ ] First-run experience: no config → creates Ollama default, prints the two-line
      "point your client here" instructions.
- [ ] README: what/why, 30-second quickstart per client (Ollama CLI, OpenAI SDK, Aider,
      Cline), screenshots, the "vessel.db contains your prompts" privacy note (§8).
- [ ] MIT license, CI (build + tests + publish artifacts per RID), versioned releases.
- [ ] Pre-release pass: bind-address banner (§8), error messages, empty states.

**Done when:** a stranger with Ollama installed goes from download to seeing their first
captured request in under two minutes, without reading more than the quickstart.

---

## Risks to watch

| Risk | Mitigation |
| --- | --- |
| Streaming tee subtly alters timing/bytes (the whole product's credibility) | Phase 0 byte-identical verification; keep `vessel_overhead_ms` honest and visible |
| Format drift (providers change SSE shapes) | Golden-file fixtures from real captures; raw fallback means drift degrades gracefully, never breaks proxying |
| Trimming breaks YARP/SQLite at publish time | Test single-file publish in Phase 0, not Phase 6 |
| Scope creep before daily-drive value | Phases 0–3 are the product; everything after is optional in any order |
