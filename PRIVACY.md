# Privacy Policy

**Product:** Vessel, the local-first observability proxy for LLM traffic
**Publisher:** Spencer Clark
**Effective:** 29 September 2026

Vessel is software you run on your own machine. This policy explains what it records,
where that data lives, who can reach it, and where it is sent.

## The short version

- Vessel stores the LLM traffic it proxies (prompts, responses, metadata and headers) in
  a local database on your machine.
- Vessel sends that traffic only to the model providers **you** configure. If you
  configure a remote provider such as OpenAI or Anthropic, your prompts go to that
  provider, and that provider's privacy policy applies to them.
- Vessel makes no network calls of its own. There is no telemetry, analytics, crash
  reporting, update check or account. The publisher never receives any of your data.

## What Vessel captures

For every request sent through the proxy, Vessel records:

- the request and response bodies (your prompts and the model's responses), in full,
  up to the per-body capture cap (`capture.maxBodyMb`, 32 MB by default; larger bodies
  are truncated in the stored copy and flagged, never in the traffic itself);
- request metadata: method, path, backend, model, status, timings, token counts,
  session name and any warnings;
- request and response headers, with secrets redacted as described below.

Capturing this is the purpose of the product. Treat the database as you would the
prompts themselves.

## Where it is stored

Everything is kept in a data folder on your machine:

| Platform | Location |
|---|---|
| Windows | `%LOCALAPPDATA%\vessel-proxy` |
| macOS | `~/Library/Application Support/vessel-proxy` |
| Linux | `$XDG_CONFIG_HOME/vessel-proxy` (default `~/.config/vessel-proxy`) |

If a `vessel.json` sits next to the executable (portable mode), or you pass `--config`,
the folder containing that config file is used instead.

The folder holds `vessel.json` (your settings, including backend URLs) and `vessel.db`
(the captured traffic, with SQLite's `vessel.db-wal` and `vessel.db-shm` alongside).
Bodies are compressed but not encrypted. Vessel writes nothing anywhere else.

## Retention and deletion

- Vessel keeps at most 10,000 requests and a 500 MB database by default. When either
  cap is exceeded, the oldest requests are deleted automatically. Both caps are
  settings (`retention.maxRequests`, `retention.maxDbSizeMb`) you can change in the UI.
- You can clear all history, or bulk-delete every session except the current one, from
  the UI's Data panel. Individual sessions can be deleted from the session picker.
- To remove everything, delete the Vessel executable and the data folder above. Vessel
  keeps no other files, registry entries or remote copies.

## Header redaction

Before a request is stored, the values of these headers are redacted: `Authorization`,
`Proxy-Authorization`, `X-Api-Key`, `Api-Key`, `Cookie` and `Set-Cookie`. The stored
value keeps only the scheme and the last four characters (for example
`Bearer …Ab4x`); for secrets of eight characters or fewer, only the scheme is kept. Redaction
applies to the stored copy only; the request forwarded to your provider is unchanged.

Vessel never stores API keys. Replay reads a key from an environment variable you name
(`authEnv` on a backend, or `OPENAI_API_KEY` / `ANTHROPIC_API_KEY` by default) at the
moment it sends, and does not write it to disk.

## Model providers you configure

Vessel is a reverse proxy. It forwards each request to the backend you configure and
returns the response. Backends can be local (Ollama, vLLM, llama.cpp, LM Studio and
similar) or remote (OpenAI, Anthropic, Google Gemini, OpenRouter and others).

- Requests are forwarded byte for byte. Vessel only removes its own `X-Vessel-*`
  control headers. The one exception is the opt-in `injectStreamUsage` setting, which
  asks OpenAI-format backends to include token usage in streamed responses.
- When a backend is remote, the prompts and responses sent through it are handled by
  that provider under that provider's own terms and privacy policy. Vessel's
  local-only storage does not change what a remote provider receives or keeps.
- Vessel rejects `http://` URLs for remote backends, so an API key is never sent to a
  public host in plaintext. Local and LAN backends may use `http://`.

## Replay and Compare

Replay re-sends a captured request, optionally edited, to a backend and model you
choose, and can fan out to up to eight variations at once. Each replayed request is a
new request to that backend, carries the prompt to it exactly as a normal request
would, and is captured and stored like any other. Compare displays those responses side
by side and sends nothing itself.

## Network binding

Vessel listens on `127.0.0.1:4550` by default, so only programs on your own machine can
reach it. If you configure a non-loopback address (for example `0.0.0.0`, or when
running in a container), anyone who can reach that address on your network can use the
proxy, open the UI and read your captured traffic. Vessel shows a warning in the UI and
in its startup log when it is bound this way.

## Access through the UI and MCP

The web UI (`/vessel/`) and the read-only MCP endpoint (`/vessel/mcp`) are served on the
same address as the proxy and have no login. Anyone who can reach that address can read
captured prompts and responses through them.

- The MCP endpoint is **enabled by default**. Any MCP client you connect to it, such as
  an AI coding assistant, can read your captured traffic, and may pass what it reads to
  that client's own model provider. Turn it off with `mcp.enabled: false` or in the UI;
  the change applies immediately.
- To stop other websites open in your browser from reaching Vessel, the UI, API and MCP
  endpoint only answer requests addressed to a loopback host or the configured listen
  address, and state-changing API calls from a browser must come from Vessel's own page.
  This is a safeguard, not authentication.

## This website

vesselproxy.app is a static page. To show the latest version number, your browser asks
GitHub's public API (`api.github.com`) for the latest release; GitHub's privacy
statement applies to that request. The site sets no cookies and runs no analytics.

## Changes

Changes to this policy are made in this file in the Vessel repository, so its full
history is public. The effective date above changes with each revision.

## Contact

Questions about this policy: [open an issue](https://github.com/spenceclark/Vessel/issues).
