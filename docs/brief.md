# Vessel — Product Brief

> The lightweight observability reverse proxy for LLM traffic.
> Single binary. Point a `base_url` at it; get capture, metrics, and a UI.

This is the **what and why**. The **how** lives in [architecture.md](architecture.md)
(design authority) and [plan.md](plan.md) (phased delivery); per-phase specs
(e.g. [phase-0.md](phase-0.md)) are written as each phase begins. If this document ever
conflicts with architecture.md, architecture.md wins.

## What it is

A local-first reverse proxy that sits between LLM clients (agents, SDK scripts, dev
tools) and LLM backends. It forwards traffic as-is, captures every request/response
including streamed ones, and serves an embedded web UI to browse, search, and analyze
them. Initially for the author's own use (99% Ollama); open-sourced under MIT if it
proves useful.

**Not** a load balancer, API gateway, MITM proxy, or hosted service. It never mutates
traffic (beyond stripping its own `X-Vessel-*` control headers) and never holds API keys.

## Core principles

- **Zero-config drop-in** — run one self-contained executable, change one base URL.
  With Ollama as the default backend it's a literal drop-in replacement endpoint.
- **Forward-as-is** — what the client sent is what the backend receives.
- **Negligible overhead** — unbuffered streaming pass-through; persistence on a
  background writer; Vessel measures and displays its own per-request overhead.
- **Graceful degradation** — unrecognized traffic is still proxied untouched and still
  captured as raw bytes with timing. Silently: nothing is ever dropped or rejected.
- **Local and private** — binds localhost by default; auth headers redacted at rest
  (scheme + last 4 chars, recognizable for debugging).

## Supported traffic (v1)

| Format | Covers |
| --- | --- |
| OpenAI chat completions | Ollama `/v1`, LM Studio, llama.cpp `llama-server`, Unsloth, any OpenAI-compatible server, OpenAI live API |
| Anthropic messages | Anthropic live API, Ollama's Anthropic-compat endpoint |
| Ollama native (`/api/chat`, `/api/generate`) | Ollama's own API — first-class, incl. its exact token/timing stats |
| Raw fallback | Everything else — proxied and captured as-is |

Remote HTTPS backends (OpenAI/Anthropic live) work as ordinary outbound TLS from the
proxy; clients speak plain HTTP to localhost.

## Backends, routing, tags

- Multiple named backends configured in advance; one is the **default**.
- Per-request routing by path prefix (`/b/{backend}/…`) or header
  (`X-Vessel-Backend`); anything else goes to the default.
- Free-form **tags** (e.g. agent name) via `X-Vessel-Tags` header or `/t/{tags}/…`
  path prefix; shown in the UI and filterable.

## UI (embedded, web-based)

- **Session stats bar** — total/failed requests, avg latency, avg tok/s, avg TTFT;
  "Reset session" at any time (history is preserved).
- **History** — reverse-chronological virtualized list with live in-flight requests;
  free-text search (full-text over prompts/responses) and filters (backend, model, tag,
  status, format, warnings).
- **Detail view** — metrics (duration, TTFT, tok/s, token counts incl. cache read/write,
  Vessel overhead, rate-limit headers, cost estimate), headers, rendered prompt and
  response, tool calls as readable cards, raw JSON / raw stream toggles.
- **Warning badges** — truncated responses (`stop_reason: length/max_tokens`), errors,
  estimated-token counts, cold model loads, slow TTFT.
- **Replay** — re-send any captured request, optionally against a different
  backend/model; plus copy-as-curl. **Diff** any two requests side by side.
- **Ollama extras** — loaded models / memory (`ollama ps`), server.log viewer.

## MCP (read-only)

A built-in Model Context Protocol server (`/vessel/mcp`, off-switchable) lets your
own AI tools interrogate captured traffic directly — search, read windowed text,
stats, sessions — no browser needed. Read-only by design; mutating tools (replay,
clear) are a separate future decision.

## Storage

SQLite, single file, written off the request path. Persistent, with easy clear-down and
configurable caps (max requests / max DB size) so it never grows unbounded. Bodies
compressed; full prompts stored by design — the README will state plainly that the DB
contains your prompts.

## Tech (decided)

.NET + YARP on Kestrel, SQLite (WAL + FTS5), embedded React + Vite + Tailwind +
shadcn/ui SPA, SSE live feed. Shipped as a self-contained single-file executable per
platform — no runtime install; an official container image exists for compose
stacks, but Docker is not required. Rationale and alternatives
considered: architecture.md §12.
