# Changelog

All notable changes are documented here. Vessel follows Semantic Versioning.

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
