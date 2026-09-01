# Changelog

All notable changes are documented here. Vessel follows Semantic Versioning.

## 0.2.0 — 2026-08-31

### Added
- Reports view: a History/Reports toggle in the header opens a full-width charts panel —
  context growth (tokens in per request over a session, groupable by tag/model), tokens by
  model/tag, requests by model/tag, avg tok/s by model, duration percentiles (p50/p95) by
  tag, cache efficiency by model, and warnings by type (#25, #26). Reports carries the
  History filters as clearable chips, and clicking a chart point jumps to that request.
- Context growth defaults its grouping to Tag once the scope has any tagged traffic (None
  stays a plain scatter — connecting unrelated interleaved agents' requests with a line
  drew a sawtooth, not growth); an Overlay/Grid toggle switches a grouped chart to one
  mini-chart per tag/model on a shared y-axis when there's more than one series to
  separate; every chart legend supports click-to-isolate-this-series and shift-click-to-
  hide-just-this-one (#25 live-use feedback).
- A single-member grouping (e.g. one model in scope) either drops its card entirely, when
  the number would just restate the header stats bar (Tokens/Requests/Avg tok/s by
  model/tag), or collapses it to a small stat panel — the group's name plus its numbers as
  labeled tiles, echoing the header's own stat look — when it carries something the header
  doesn't (Duration by tag's p50/p95, Cache efficiency's cached-% ratio, Warnings by
  type's code breakdown) (#26 live-use feedback).
- `GET /vessel/api/series` and `GET /vessel/api/aggregate` — the read endpoints behind the
  Reports charts, scoped by the same session/filter predicate as `/requests`. `/aggregate`
  additionally accepts `by=warning` (a request counts once per warning code it carries) and
  every row now carries nearest-rank `p50DurationMs`/`p95DurationMs`.
- ui-spec §2.3: chart color tokens (`--chart-grid`, `--chart-axis`, `--chart-1`…`--chart-6`,
  the latter riding the existing tag ramp) and the chart form rules (line/area, horizontal
  bar, no pie/3D/gradients, scatter for unrelated per-event data, small multiples for
  series that are meaningful alone but not together).

### Fixed
- History filters (text/backend/model/tag/status/warnings) now reset whenever the session
  scope changes — via the session picker, Reset session, or a server-driven correction —
  instead of silently carrying a filter that applied to a different session's traffic.
- The Context Growth Overlay/Grid toggle no longer gets stuck on Grid after switching
  group-by (or session) to one with only a single series: it correctly falls back to the
  full-width overlay chart instead of rendering one lone mini-chart in the small-multiples
  grid layout.

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
