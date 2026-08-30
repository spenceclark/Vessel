# Changelog

All notable changes are documented here. Vessel follows Semantic Versioning.

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
