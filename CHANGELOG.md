# Changelog

All notable changes are documented here. Vessel follows Semantic Versioning.

## 0.3.0 — 2026-09-19

Tools tab, Anthropic server tools, TypeSafe System One, MCP resources, and two new warnings.

### Added

- Tools tab: a request that declares tools gets a `Tools (n)` tab with one card per tool —
  description, parameter table, and how many times it was called (#86).
- Anthropic server-tool blocks (`server_tool_use`, web search and web fetch results) render
  as cards, with numbered citations and a sources list per message (#88).
- Tool call args and tool results that parse to an object render as a readable field list,
  with multi-line strings unescaped; Raw JSON still shows the exact bytes (#91).
- TypeSafe System One (Jev) support: `typesafe-systemone` traffic — direct `/v1/systemone` or
  OpenRouter's `/api/alpha/decisions` — is detected instead of falling to `raw`, with a
  Decisions view (one card per question, probability bars) and same-backend replay (#122).
- Two new warnings: `repetitive_output` for a response stuck in a loop, and
  `slow_response` for a non-streamed request slower than `warnings.slowResponseMs`
  (default 120s, `0` disables; editable in the Config panel) (#90).
- MCP resources: `vessel://requests/{id}` and `vessel://sessions/{id}`, with
  `resources/list` returning the most recent requests and sessions (#92).
- OpenRouter and TypeSafe in the "Add backend" catalog (#124).

### Fixed

- A queued replay whose backend was repointed while it waited is refused with
  `409 replay_target_changed` instead of sending the old credential to the new
  destination (#106).
- MCP request reads decode bodies under `capture.maxBodyMb` rather than unbounded, and
  report truncation with `decodeTruncated` (#107).
- While a listen-address change is pending restart, the UI/API stays reachable on the
  address the process is actually bound to (#112).
- `path_missing_v1` only fires for the missing-`/v1` mistake: an OpenAI SDK path suffix, no
  `/v1/` segment anywhere, and a backend `baseUrl` with no path of its own (#123).
- Anthropic server-tool blocks are included in MCP `include=text` and full-text search;
  `encrypted_*` fields never reach the flattened text (#89).
- `openai-chat` reasoning text under the `reasoning` key (e.g. Ollama) is no longer dropped
  from the thinking block and search (#80).
- Anthropic rate-limit headers are parsed (#108).
- Parameter sweeps on OpenAI Responses replays use `max_output_tokens` (#111).
- Config validation rejects a backend with `"type": null` (#109).
- Multi-replay rejects a null variation with `invalid_request` and the offending index,
  before dispatching anything (#110).

### Changed

- Dependency bumps: react 19.3.0, tailwind-merge 3.7.0 (#117, #119).

## 0.2.1 — 2026-09-13

Post-0.2.0 fixes.

### Fixed

- Ctrl+C no longer hangs on shutdown, and the startup console output is clearer (#64).
- The live request feed reconnects when the browser's EventSource enters CLOSED instead
  of silently going stale (#75).
- A backend connection failure logs one clean warning line instead of a YARP stack
  trace (#76).

### Changed

- Dependency bumps: Microsoft.Data.Sqlite 10.0.12, lucide-react 1.43.0,
  @tanstack/react-virtual 3.14.11 (#68, #69, #72).

## 0.2.0 — 2026-09-07

Named sessions, multi-replay with scoring, reports, export, and package-manager installs.

### Added
- Named sessions: assign requests to a run with `X-Vessel-Session` (created on first use),
  with a session picker, per-session stats, and session deletion (#29, #41).
- Multi-replay: fan one captured request across several models, or one model across
  several parameter values, and see every response side by side with per-column metric
  deltas (#48).
- Scoring: rate any response 1–5 from the Compare view — original included — and a
  leaderboard in Reports ranks models and parameter sets by mean score and win-rate (#49).
- Reports view: requests, tokens, tok/s, duration, cache efficiency, and warnings by
  model/tag/backend, plus a context-growth chart per session or tag (#25, #26).
- Export the current filtered request list to CSV or JSONL (#24).
- Replay dialect fix-ups: `max_tokens` ↔ `max_completion_tokens` is renamed automatically
  for OpenAI Chat replays and shown as `(auto)` in Compare's parameter diff (#28).
- Package-manager installs: Homebrew (macOS and Linux) and Scoop (#31, #32).
- Tool calls a model emits as plain text instead of structured output are detected and
  flagged (#40).
- A hint on 404s from OpenAI-compatible backends when the client's `base_url` is missing
  `/v1` (#57).
- README: tagging agents and naming runs from LangChain / LangGraph.

### Changed
- Compare is now a grid rather than a fixed pair (a pair is the two-column case); metrics
  render as a table across columns (#48).
- MCP `search_requests` and `get_request` include each request's score and replay linkage
  (#48, #49).
- Config panel: backend cards compacted to two rows with shared explainers (#42).
- macOS unblock steps updated for Sequoia (#39).

The database schema migrates automatically on first start; the additions are additive, so
an older Vessel can still open the file.

## 0.1.1 — 2026-08-30

Post-launch fixes and polish.

### Fixed
- Copy-as-curl now emits the correct auth for Anthropic backends (`x-api-key` +
  `anthropic-version`) rather than an OpenAI `Bearer` header — including backends left on
  `type: auto` (#7).
- Copy-as-curl targets a reachable host instead of the bind address, so a command copied
  while Vessel runs in a container actually connects (#8).
- Chrome DevTools' `/.well-known/appspecific/…` probe is no longer proxied and captured as
  an errored request (#4).

### Added
- Config validation rejects `http://` for public/remote backends, so an API key is never
  sent in plaintext; localhost and LAN backends are unaffected (#5).
- Keyboard navigation — ↑/↓ move through the request list (#6).
- "Add backend" is now a picker of known backends (OpenAI, Anthropic, Ollama, vLLM,
  llama.cpp, LM Studio, …) that prefills URL, type, and auth env var (#9).
- First-run guidance when Ollama isn't reachable, pointing you to add a backend (#11).

### Changed
- Clearer label and help text for the streamed-token-usage setting (#10).

## 0.1.0 — 2026-08-29

First public release of Vessel: a local-first, single-binary reverse proxy for LLM
traffic. It captures and searches OpenAI, Anthropic, Ollama, and raw traffic; provides
live metrics, replay and Compare; includes a read-only MCP endpoint; and ships native
Windows, Linux, and macOS archives plus a GHCR container image.
