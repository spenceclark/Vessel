# vessel

> The lightweight, local-first observability proxy for LLM traffic. Point a client’s
> `base_url` at one small binary and get capture, search, metrics, replay, Compare, and
> a private UI—without sending prompts to a third party.

![Main Screen Shot](docs/assets/main_screen.png)

## Quickstart

1. Install and run `vessel`:

   ```bash
   # macOS (Apple Silicon) and Linux — Homebrew 6 requires trusting a third-party tap first
   brew trust spenceclark/tap
   brew install spenceclark/tap/vessel

   # Windows
   scoop bucket add spenceclark https://github.com/spenceclark/scoop-bucket
   scoop install vessel
   ```

   Or download the archive for your OS from [Releases](https://github.com/spenceclark/Vessel/releases), extract it, and run `vessel` (or `vessel.exe`). A first run creates the config and opens `http://127.0.0.1:4550/vessel/` (`--no-open` skips the browser).
   The default backend is Ollama on `localhost:11434`; if nothing is listening there, that first run opens on the backend picker so you can add OpenAI, Claude, or another backend straight away.
2. Point a client at Vessel. Your first request appearing in the UI means it worked.

```bash
# Ollama CLI
OLLAMA_HOST=127.0.0.1:4550 ollama run llama3.2

# OpenAI SDK — use base_url="http://127.0.0.1:4550/b/openai/v1"
# Anthropic SDK — use base_url="http://127.0.0.1:4550/b/anthropic"

# curl (Ollama-native)
curl http://127.0.0.1:4550/api/chat -d '{"model":"llama3.2","messages":[{"role":"user","content":"hello"}],"stream":false}'
```

Vessel is a **foreground process**: its terminal is Vessel, so closing the terminal
stops capture. For always-on use, use your OS’s normal mechanism (Task Scheduler,
systemd, or launchd).

Homebrew and Scoop installs skip the prompts below. If you run the downloaded archive
instead: macOS 15+ (Sequoia) removed right-click → Open as a Gatekeeper bypass, so first
unblock the binary from Terminal:

```
xattr -d com.apple.quarantine ./vessel
```

If that doesn't work, `xattr -c ./vessel` clears all extended attributes. Alternatively,
use the GUI path: run `./vessel`, let it be blocked, then go to **System Settings →
Privacy & Security** and click **Open Anyway**, then run it again and click **Open**. On
Apple Silicon, if you still see `Killed: 9` after un-quarantining, ad-hoc-sign the binary
with `codesign -s - --force ./vessel`.

Windows may show SmartScreen, where **More info** then **Run anyway** is needed.
Signing/notarization is intentionally deferred.

## Features

- Captures and searches OpenAI Chat/Responses, Anthropic Messages, Ollama, and unknown
  traffic; live history, sessions and tags, filters, backend health, warnings, and
  light/dark themes.
- Replay any captured request against another backend or model — or fan it out across
  several models or parameter values — and compare every response and its metrics beside
  the original. Score responses 1–5; Reports ranks models and parameter sets by score.
- Reports: tokens, requests, tok/s, duration, cache efficiency, and warnings by model, tag,
  or backend, plus context growth over a session. Export any filtered list to CSV or JSONL.
- Copy as curl and a read-only MCP endpoint (`/vessel/mcp`) for searches, request detail,
  stats, and sessions from your own AI tools.

## Routing and tags

Route any request to any configured backend — per request, with no client
reconfiguration. Any OpenAI-compatible server (LM Studio, llama.cpp, vLLM, …) is just
a backend entry away:

```bash
# by path — works with any client that can set a base URL
base_url = "http://127.0.0.1:4550/b/lmstudio/v1"

# by header — for your own code
curl http://127.0.0.1:4550/api/chat -H "X-Vessel-Backend: ollama" -d '...'
```

Tag requests to attribute traffic — for example, one tag per agent in a multi-agent
app — then filter, search, and compare by tag in the UI:

```bash
curl http://127.0.0.1:4550/api/chat -H "X-Vessel-Tags: DungeonMaster,run-42" -d '...'
# or the path form, for header-less clients:  /t/DungeonMaster/api/chat
```

Assign a request to a named run with `X-Vessel-Session` (created on first use). This
does not change the Reset-driven session used by headerless traffic:

```bash
curl http://127.0.0.1:4550/api/chat -H "X-Vessel-Session: run-42" -d '...'
```

Session names are limited to 128 characters, and named sessions are capped at 500
markers: at that cap, requests for unseen names are captured in the current session (Vessel
logs a warning when this happens), while existing named sessions continue to work. (Reset-created markers are not subject
to this cap.)

Both compose: `/b/ollama/t/planner/api/chat`. Routing precedence is `/b/{backend}/…`,
then `X-Vessel-Backend`, then the default backend. Vessel strips its own `X-Vessel-*`
headers before forwarding — backends never see them.

### From LangChain / LangGraph

LangChain chat models don't expose per-request headers, but nothing above needs them.
Per-agent tags ride on the path — one model instance per agent:

```python
planner = init_chat_model('ollama:llama3.2', base_url='http://127.0.0.1:4550/t/planner')
```

For per-run sessions (and node tags from one shared model), stamp the outgoing request
from a callback. LangGraph puts the node name in run metadata; put the `thread_id` there
too, and it becomes the Vessel session:

```python
import contextvars, httpx
from langchain_core.callbacks import BaseCallbackHandler

_vessel: contextvars.ContextVar[dict | None] = contextvars.ContextVar('vessel', default=None)

class VesselCallback(BaseCallbackHandler):
    def on_chat_model_start(self, serialized, messages, *, metadata=None, **kwargs):
        md = metadata or {}
        _vessel.set({k: v for k, v in {
            'X-Vessel-Tags': md.get('langgraph_node'),
            'X-Vessel-Session': md.get('thread_id'),
        }.items() if v})

def _stamp(request: httpx.Request):
    request.headers.update(_vessel.get() or {})

llm = init_chat_model('ollama:llama3.2', base_url='http://127.0.0.1:4550',
                      client_kwargs={'event_hooks': {'request': [_stamp]}})

thread_id = str(uuid.uuid4())
graph.invoke(state, config={'configurable': {'thread_id': thread_id},
                            'metadata': {'thread_id': thread_id},
                            'callbacks': [VesselCallback()]})
```

`ChatOpenAI` takes the hook as `http_client=httpx.Client(event_hooks=…)` instead of
`client_kwargs`; for `ainvoke`, give the async client an `async def` hook. A complete
four-agent LangGraph example is in [`docs/examples/langgraph.py`](docs/examples/langgraph.py).

## Query your traffic from AI tools (MCP)

Vessel serves a read-only [MCP](https://modelcontextprotocol.io) endpoint, so tools
like Claude Code can search and inspect your captured traffic — "why did my planner
agent stall this afternoon?" answered by the agent querying Vessel directly:

```bash
claude mcp add --transport http --scope user vessel http://127.0.0.1:4550/vessel/mcp
```

`--scope user` makes it available in every project (omit it to register for the
current folder only). The endpoint exposes search, request detail, stats, and
sessions — read-only, and any MCP client you connect can read your captured prompts.
Disable it with `mcp.enabled: false`.

## Container / compose

For Docker users, the shipped [`compose.yaml`](compose.yaml) is the canonical setup:

```bash
docker compose up -d
```

It runs `ghcr.io/spenceclark/vessel`, persists state in `vessel-data`, and assumes Ollama
runs on the host. In Open WebUI, set `OLLAMA_BASE_URL=http://vessel:4550` when Open WebUI
runs in the same compose stack (use `http://localhost:4550` from a host-side Open WebUI);
the path is Open WebUI → Vessel → Ollama. For bare Docker, `host.docker.internal` is the
default backend host name (and is supplied by the compose file’s `host-gateway` mapping).

To run Ollama in the same compose stack, uncomment the commented service in
`compose.yaml`, then change Vessel’s backend `baseUrl` to `http://ollama:11434`.

## Configuration

`--config <path>` always wins. Otherwise, an existing `vessel.json` beside the executable
selects portable mode; a fresh download creates one under `%LOCALAPPDATA%\vessel-proxy`
(Windows), `~/.config/vessel-proxy` (Linux/XDG), or
`~/Library/Application Support/vessel-proxy` (macOS). `vessel.db` lives alongside it.
`vessel --help` prints the resolved paths; `--version` prints the build; `--no-open` skips
the first-run browser.

<!-- config-fields: backends.authEnv backends.baseUrl backends.injectStreamUsage backends.type capture.maxBodyMb defaultBackend listen mcp.enabled retention.maxDbSizeMb retention.maxRequests timeouts.activitySeconds warnings.slowTtftMs -->

| Field | Meaning |
| --- | --- |
| `listen` | `host:port`; defaults to `127.0.0.1:4550` (or `0.0.0.0:4550` in a container). |
| `defaultBackend` | Backend name used when no route selector is supplied. |
| `backends.<name>.baseUrl` | Required HTTP(S) backend URL. |
| `backends.<name>.type` | `ollama`, `openai`, `anthropic`, or `auto`; informs parsing and replay. |
| `backends.<name>.injectStreamUsage` | For OpenAI backends, request exact streamed usage; off by default. |
| `backends.<name>.authEnv` | Optional process environment variable used only for replay credentials. |
| `timeouts.activitySeconds` | Maximum no-byte-movement interval (default `1800`). |
| `retention.maxRequests` / `retention.maxDbSizeMb` | Local history caps (defaults `10000` / `500`). |
| `capture.maxBodyMb` | Per-body capture cap (default `32`); forwarding is never truncated. |
| `warnings.slowTtftMs` | Slow-TTFT threshold in ms (default `5000`); `0` disables it. |
| `mcp.enabled` | Enables the read-only MCP endpoint (default `true`). |

Example:

```json
{
  "listen": "127.0.0.1:4550",
  "defaultBackend": "ollama",
  "backends": {
    "ollama": { "baseUrl": "http://localhost:11434", "type": "ollama" },
    "openai": { "baseUrl": "https://api.openai.com", "type": "openai", "authEnv": "OPENAI_API_KEY" }
  }
}
```

## Replay and Compare

Open any captured request and choose **Replay** to send it again — to the same backend,
a different one, or a different model. Replay stays within one wire format: any
OpenAI-compatible backend can stand in for another, but Ollama-native ⇄ Anthropic is not
translated. The one rename that would otherwise make replays fail is applied for you —
`max_tokens` ↔ `max_completion_tokens` for OpenAI Chat — and shown as `(auto)` in the
parameter diff.

A replay can fan out: up to 8 variations in one go, either several models or one
parameter swept across several values (temperature `0.2, 0.7, 1.0`). Members run one after
another so timings aren’t polluted by contention, and the dialog says how many requests it
will send — and how many go to keyed backends — before anything goes out. Compare shows
each response beside the original with metric deltas and the parameters that differed.

Score any response 1–5 from Compare (keys `1`–`5`; `←`/`→` move between columns, `0`
clears). Reports ranks models and parameter sets by mean score and how often each came
top. In the request list, `↑`/`↓` move the selection.

Replays carry the original’s tags, land in the current session, and link back to the
original. Their `X-Vessel-Replay-*` headers are stripped before forwarding like every
other `X-Vessel-*` header.

### Replay auth

Vessel never stores keys. Replay reads the credential from the environment of the Vessel
process: `OPENAI_API_KEY` for OpenAI, `ANTHROPIC_API_KEY` for Anthropic, or the backend’s
`authEnv` name for another compatible backend.

## Reports and export

**Reports** charts the current scope — session, tags, dates — as tokens, requests, tok/s,
and duration by model, tag, or backend; cache efficiency; warnings by type; context growth
across a session; and the score leaderboards. **Export**, in the filter bar, writes exactly
the filtered list (the row count is shown first) to CSV, or to JSONL with bodies included
if you want them.

## Warnings

Rows carry a warning count; the Overview tab names each one.

| Warning | Meaning |
| --- | --- |
| `cold_load` | Ollama loaded the model for this request — a slow duration, not slow generation. |
| `slow_ttft` | Time to first token exceeded `warnings.slowTtftMs`, and no cold load explains it. |
| `truncated_response` | The response was cut short by the output limit (`length` / `max_tokens`). |
| `tokens_estimated` | The backend reported no usage; counts are estimated (chars ÷ 4). |
| `usage_injected` | Vessel added `stream_options.include_usage` (the opt-in `injectStreamUsage`). |
| `tool_call_in_text` | The request declared tools, but the model wrote a tool call as plain text instead of a structured call. Detection only — nothing is rewritten. |
| `path_missing_v1` | 404 from an OpenAI-compatible backend on a path without `/v1/` — put `/v1` in the client’s `base_url`. |
| `stream_incomplete` | A streamed response never reached its terminal marker. |
| `client_disconnect` | The client went away before the exchange completed. |
| `body_truncated` | The stored body hit `capture.maxBodyMb`; forwarding was not truncated. |
| `http_error` / `proxy_error` | Non-2xx from the backend / Vessel could not reach it. |
| `parse_error` | Format detection or parsing failed; the row was kept as `raw` with bytes intact. |

## Privacy and data

Requests are forwarded **byte-for-byte** — Vessel never modifies traffic beyond
stripping its own `X-Vessel-*` control headers (the opt-in `injectStreamUsage` is the
single documented exception). Vessel’s own per-request overhead is measured and shown
on every request in the UI — typically around a millisecond.

`vessel.db` contains your prompts and responses, stored locally with compressed bodies.
Authorization headers are redacted at rest, and Vessel never stores API keys anywhere.
Vessel binds localhost by default; if you bind a non-loopback address, the UI and
startup log warn that people on the network may read captured prompts (and access MCP
when enabled). Keep retention caps appropriate, and use the UI’s Data panel to clear
history or bulk-delete non-current sessions; individual sessions can be deleted from the
session picker. To remove Vessel completely: delete the executable and the `vessel-proxy`
data folder — there is nothing else.

## Building from source

Requires .NET 10 and Node.js.

```bash
dotnet build Vessel.sln
dotnet test Vessel.sln
cd frontend && npm ci && npm test && npm run build && npm run lint
dotnet publish src/Vessel -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

See [`docs/brief.md`](docs/brief.md) for the product overview,
[`docs/architecture.md`](docs/architecture.md) for the design, and
[`CONTRIBUTING.md`](CONTRIBUTING.md) to get involved. Vessel is licensed under
[MIT](LICENSE).
