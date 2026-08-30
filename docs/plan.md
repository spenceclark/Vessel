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

## Phase 5 — The comparison loop

*The minimum story worth open-sourcing: "same prompt, different model/backend, zero
client changes." Replay and diff share machinery and UI; they ship together. This is
deliberately the whole phase — everything else moved to 7/8 so the launch (Phase 6)
happens behind a tight, demoable story rather than after a long feature tail.*

- [x] **Replay** (§7): re-send with optional backend/model override; `replay_of` link both
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
- [x] **Compare** (narrowed from "Diff" by decision): renders a **`replay_of` pair
      only** — original vs its replay, side-by-side responses + metric deltas +
      param diff. Not an arbitrary two-row picker; no inline word-diff (two sampled
      generations differ everywhere — the diff would render noise as signal).
- [x] ~~**Warning badges polish**~~ — already delivered: `cold_load` detection
      (Phase 2 D6) and the configurable `warnings.slowTtftMs` threshold both landed;
      badges render per ui-spec. Absorbed, no remaining work.

Spec: [phase-5.md](phases/phase-5.md).

**Done when:** replay + Compare let you answer "would qwen handle what opus handled?"
without touching client code.

---

## Phase 5b — MCP server (pre-launch, pulled forward from Phase 7)

*Read-only interrogation of captured traffic by the user's own AI tools — pulled
ahead of launch because it is one small session on the existing read store and
"works with Claude Code out of the box" is a launch-day differentiator. Everything
mutating (replay/clear via MCP) stays a separate future decision.*

- [x] Streamable HTTP endpoint `/vessel/mcp` (official ModelContextProtocol C# SDK,
      same host, D03 guards; `mcp.enabled` kill-switch, live-applied).
- [x] Four read-only tools: `search_requests` (FTS + filters, compact rows, no
      bodies), `get_request` (windowed flattened text, self-describing truncation),
      `get_stats`, `list_sessions`.
- [x] Token-budget shaping: conservative defaults, hard caps, binary never inlined —
      a 200K-token context must never arrive in one tool result.
- [x] Verified against a real MCP client (Claude Code) on real traffic — manual gate item 1 remains for the user.

Spec: [phase-5b.md](phases/phase-5b.md).

**Done when:** "find my truncated requests from today and tell me why" works from
Claude Code against a running Vessel.

---

## Phase 6 — Ship / open-source

*Deliberately before the remaining features: the product is complete as
observe-and-compare, and everything in Phases 7–8 gets better with real users' feedback
(and makes good headline releases for an open-source project's cadence).*

- [x] Self-contained single-file publish for win-x64, linux-x64, osx-arm64
      (osx-x64 dropped for v0.1 — see phase-6.md §6); trimming on per phase-6 D1,
      with the MCP SDK's trim-safety (the Phase 5b blocker) resolved first —
      all-RIDs-untrimmed is the recorded fallback.
- [x] First-run experience: no config → creates the Ollama-only default (reconfirmed by
      Phase 5 PD1), then prints the two-line "point your client here" instructions.
- [x] README: what/why, 30-second quickstart per client (Ollama CLI, OpenAI SDK, Aider,
      Cline), screenshots, the "vessel.db contains your prompts" privacy note (§8),
      replay-auth env-var conventions.
- [x] MIT license, CI (backend tests already run on `ubuntu-latest` + `windows-latest`
      per Phase 5 PD4; add build + publish artifacts per RID), versioned releases.
- [x] Landing page (phase-6 D9): one static page in `site/` on `vesselproxy.app`,
      design system verbatim, deployed via Cloudflare Pages (no build step).
- [x] Container image on GHCR (phase-6 D10): linux amd64 (arm64 if the cross-publish
      is clean), `/data` volume convention + container-aware first-run, shipped
      `compose.yaml`, container smoke in `release.yml`.
- [x] Pre-release pass: bind-address banner (§8), error messages, empty states.

Spec: [phase-6.md](phases/phase-6.md).

**Done when:** a stranger with Ollama installed goes from download to seeing their first
captured request in under two minutes, without reading more than the quickstart.

---

## Phase 7 — Interrogation & analysis (post-launch)

*Data-out and insight features — the natural first post-launch releases, and the ones
community feedback should shape. (MCP moved to Phase 5b, pre-launch.) Candidates for
a future MCP v2 — mutating tools (replay via MCP), pending its own approval — also
live here.*

- [ ] **Export to CSV/JSONL**: a filtered/date range of requests exported with or
      without bodies, for offline analysis by user/AI. Pairs with MCP (interactive
      vs. bulk data-out).
- [ ] **Context-growth chart**: `tokens_in` over time per session/tag — makes agent
      context bloat visible at a glance. First chart: add chart tokens to ui-spec §2
      first (per §2.2's rule).
- [ ] **Additional reporting**: tokens by agent/tag, tokens by model, etc. — likely a
      small library of canned queries + visualisations on the same chart foundation.
- [ ] **Cost estimates**: static pricing table + `pricing` config overrides, `~$0.0042`
      on Overview and session totals; clearly labeled estimate (§9). (Only matters to
      live-API users — let post-launch demand prioritize it.)
- [ ] **Replay dialect fix-ups**: a tiny, documented table of known *intra*-format
      param renames applied when composing a replay for a target that requires them —
      canonical entry: `max_tokens → max_completion_tokens` for openai-type targets
      (reasoning-era models reject the old name; observed live). Principled because
      forward-as-is does not bind replay — replay is a request Vessel composes (it
      already rewrites `model`). **Transparency required:** every applied fix-up
      appears in Compare's param-diff as `max_tokens → max_completion_tokens (auto)`.
      List stays minimal and mechanical; anything needing judgment (sampler
      semantics, `num_predict` mapping) belongs to Phase 8's transformers.
- [ ] **Named sessions via header + session picker**: `X-Vessel-Session: {name}` —
      each request is assigned individually to the named session (created on first
      sight), while headerless traffic keeps falling into the Reset-driven current
      session. Explicitly NOT a global switch: two concurrent agents with different
      session names must coexist without thrashing (per-request assignment, decided
      up front). Capture stamps the session *name*; the writer resolves
      name→id at insert (lookup-or-create on the writer thread — the
      single-writer invariant does the locking for free). Header stripped before
      forwarding like all `X-Vessel-*`. UI: the Current/All toggle becomes a
      **session picker** over `GET /sessions` (newest first, names shown) —
      this also closes the existing gap that session history is fully captured but
      unbrowsable. Earns its place over tags because sessions carry the stats
      machinery (per-session averages + token totals) and one-per-request identity;
      "full stats readout for run-42" is the thing tags can't do. Replay keeps
      landing in the current session (unchanged). MCP benefits for free
      (`list_sessions`/`sessionId` already exist).
      **Motivating case (the author's "Monsters and Models" project): 8 agents talk
      to each other in one bounded run, tags carry the agent names, the run ends —
      session = the run, tag = the agent; picker + tag filter = one agent's trace
      within one run.** The orchestrator mints the run id once at startup and sets
      the header on every client it creates. Interim workaround until this lands:
      a run-id as a second tag (`X-Vessel-Tags: Brakka,run-42`) gives per-run
      filtering today — everything except per-run stats and the picker.
- [ ] **Tool-call fumble detection**: new warning `tool_call_in_text` — the request
      defined tools but the response carries tool-call-shaped JSON in its *text*
      content with no structured `tool_calls`/`tool_use`. Detection only, in the
      enricher (writer-side; adapters already parse both sides) — badge on the row,
      filterable via warnings, and per-model fumble rates once reporting lands.
      **Explicitly decided: no auto-repair, ever** — rewriting response bodies
      breaks forward-as-is (the product's trust foundation), is near-impossible for
      streams without buffering, and hides exactly the failure Vessel exists to
      surface. The fix belongs client-side (`tool_choice`, template, model choice),
      informed by this warning. Small enough to ride along with any earlier
      adapter-touching session that has slack.
- [ ] **Homebrew tap** (`brew install spenceclark/tap/vessel`): the idiomatic,
      friction-free macOS install. Homebrew strips the download quarantine attribute,
      so there is no Gatekeeper "unverified developer" prompt — unlike the current
      unsigned-binary path, which on macOS 15 (Sequoia) forces a System Settings →
      Privacy & Security → "Open Anyway" step (the right-click→Open bypass was removed).
      Demand-driven per phase-6 scope; the highest-leverage Mac-friction reducer short
      of paid signing + notarization.
- [ ] **Windows winget / scoop manifest**: the Windows analog of the tap — sidesteps
      the SmartScreen "unrecognized app" prompt an unsigned download otherwise shows.
- [ ] **README macOS unblock steps refresh**: the current README's "right-click → Open"
      advice is stale — macOS 15 removed that Gatekeeper bypass. Lead with
      `xattr -d com.apple.quarantine ./vessel` and the Settings → Privacy & Security →
      "Open Anyway" path instead. Small; can land ahead of the tap.

---

## Phase 8 — Live & deep integrations (post-launch, higher-risk)

*Features that touch the proxy hot path or grow new product surfaces — sequenced last
deliberately, with the hot-path rules from §4.1 applying in full.*

- [ ] **Live tail**: stream the in-flight response into the UI as it generates — the
      in-flight detail's state line (ui-spec §9.1) becomes a live token view. Needs
      its own design pass: chunk broadcast only while a client has that request
      open, bounded per-request buffers, drop-never-block. The J0 event-log model is
      the foundation (chunks become high-frequency events on the same ordered feed).
- [ ] **Cross-provider replay transformers**: ollama-native ⇄ openai-chat ⇄
      anthropic-messages request translation so replay crosses wire formats, incl.
      the non-1:1 params (`num_predict` vs `max_tokens`). Pre-agreed as out of
      replay v1 (Phase 5 note).
- [ ] **Ollama panel**: `ollama ps` proxied view (loaded models, VRAM); server.log
      viewer if reachable (§7, §10) — a host-management surface, distinct from
      traffic observation.
- [ ] **Tray app**: the polished always-on form of "sits in front of Ollama all
      day" — system-tray icon (open UI / quit), no console window, per-OS tray
      APIs and a window-less launch mode. A real product surface; post-launch
      feedback decides whether it's wanted before it's built. Until then v0.1's
      answer stands: foreground process, OS schedulers for always-on (phase-6 D5).

---

## Risks to watch

| Risk | Mitigation |
| --- | --- |
| Streaming tee subtly alters timing/bytes (the whole product's credibility) | Phase 0 byte-identical verification; keep `vessel_overhead_ms` honest and visible |
| Format drift (providers change SSE shapes) | Golden-file fixtures from real captures; raw fallback means drift degrades gracefully, never breaks proxying |
| Trimming breaks YARP/SQLite at publish time | Test single-file publish in Phase 0, not Phase 6 |
| Scope creep before daily-drive value | Phases 0–3 are the product; everything after is optional in any order |
