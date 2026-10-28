---
name: vessel
description: Vessel, the local LLM-traffic proxy. Use to connect a codebase's LLM clients to Vessel (base URL, backend routing, agent tags, session names), or to debug an agent or LLM call from its captured traffic through Vessel's MCP tools (search_requests, get_request, get_stats, list_sessions).
---

# Vessel

Vessel is a reverse proxy, by default at `http://127.0.0.1:4550`. A client points its base URL at it; Vessel forwards the request byte-for-byte to a configured backend and captures the request, the response and its timings. This skill has two branches: **connect** a codebase to Vessel, and **debug** from what Vessel captured. Both start at step 0.

## 0. Confirm Vessel is reachable

```bash
curl -s http://127.0.0.1:4550/vessel/api/status
```

The JSON carries `listen`, `defaultBackend`, `backends[]` (`name`, `baseUrl`, `type`, `default`), `mcp.enabled` and `capture.recording`. `capture.recording: false` means traffic is forwarded but no longer captured; tell the user to restart Vessel. Connection refused means Vessel is stopped (it runs in the foreground: `vessel`) or listens elsewhere; ask the user for the address. Substitute the real `listen` address wherever this skill writes `127.0.0.1:4550`.

Done when you hold the listen address and the backend list.

## Connect a codebase

### Routing rules

- Vessel forwards to `<backend baseUrl>` + `<the path after its own prefixes>`. So a client's Vessel base URL is its original base URL with the backend's configured `baseUrl` swapped for `http://127.0.0.1:4550/b/<backend>`:
  - OpenAI SDK or any OpenAI-compatible server (backend `baseUrl` `https://api.openai.com`): `http://127.0.0.1:4550/b/openai/v1`. Keep the `/v1`; without it the backend 404s and Vessel flags `path_missing_v1`.
  - Anthropic SDK: `http://127.0.0.1:4550/b/anthropic` (the SDK appends `/v1/messages`).
  - Ollama native client or CLI, when Ollama is the default backend: `http://127.0.0.1:4550` (`OLLAMA_HOST=127.0.0.1:4550`). Ollama's OpenAI endpoint: `http://127.0.0.1:4550/v1`.
- Backend choice, first match wins: `/b/<name>/` path prefix, then `X-Vessel-Backend: <name>` header, then the default backend. An unknown name returns 404 `unknown_backend` listing the valid names.
- **Agent tags**: `X-Vessel-Tags: planner,critic` header, or a `/t/planner,critic/` path segment. In a path, `/b/` comes first: `/b/ollama/t/planner/v1`. Tags are trimmed, deduplicated and filtered by exact match.
- **Session**: `X-Vessel-Session: <name>` header. Headers are the only way to name a session; there is no path form. Vessel creates the session on first use. Names are trimmed and at most 128 characters (a longer name is ignored and the request lands in the current session). Vessel keeps at most 500 named sessions; past that, new names land in the current session. A session is one run (a conversation, a graph thread, a job), so a fresh name per request exhausts the cap.
- Vessel strips every `X-Vessel-*` header before forwarding, so any backend accepts them. API keys pass through untouched: the client keeps its own key and Vessel stores none.

### Steps

1. **Inventory every LLM client.** Search the tree for construction points and base-URL sources: `base_url`, `baseURL`, `api_base`, `OPENAI_BASE_URL`, `ANTHROPIC_BASE_URL`, `OLLAMA_HOST`, `OpenAI(`, `AsyncOpenAI(`, `Anthropic(`, `ollama.Client`, `ChatOpenAI`, `ChatAnthropic`, `ChatOllama`, `init_chat_model`, `OpenAIChatModel`, `createOpenAI`, `createAnthropic`, `litellm`. For each client record: `file:line`, wire format, the agent or role that uses it, whether agents share it, where its base URL comes from (literal, env var, config file), and whether the framework lets you set request headers per client or per call. Done when every client is in the inventory, including those in scripts, tests and config files.

2. **Propose a scheme and ask.** Draft from the inventory, then confirm each point with the user (AskUserQuestion, your recommendation first):
   - **Backend** per client: the Vessel backend whose `baseUrl` matches the client's current upstream. When none matches, the user adds one in the Vessel UI before you continue.
   - **Agent tags**: one per agent or role, named as the code names it.
   - **Session**: the run identifier the code already has (LangGraph `thread_id`, a conversation id, a job id, a uuid per process run), or no session.
   - **Switch**: always on, or opt-in behind one env var (e.g. `VESSEL_URL`) that gates the base URL and the headers together.

   Done when the user has answered all four.

3. **Wire each client.** Change the base URL where the code already sources it (env var or config before a code literal). Then attach tags and session:
   - Client can set headers: `X-Vessel-Tags` and `X-Vessel-Session`. Fixed per-agent tags suit per-client headers; a per-run session belongs in per-call headers or a client built per run.
   - Client cannot set headers: tags go in the path (`/b/<backend>/t/<tag>/v1`), one client instance per agent. A session then needs an HTTP-level hook; where none exists, tell the user that client has no session support.

   | SDK | Per client | Per call |
   | --- | --- | --- |
   | openai (Python) | `OpenAI(base_url=…, default_headers={…})` | `extra_headers={…}` |
   | openai (Node) | `new OpenAI({ baseURL, defaultHeaders })` | request option `{ headers }` |
   | anthropic (Python) | `Anthropic(base_url=…, default_headers={…})` | `extra_headers={…}` |
   | anthropic (Node) | `new Anthropic({ baseURL, defaultHeaders })` | request option `{ headers }` |
   | ollama (Python) | `Client(host=…, headers={…})` | — |
   | Vercel AI SDK | `createOpenAI({ baseURL, headers })` | `generateText({ headers })` |
   | Pydantic AI | `Agent(model, model_settings={'extra_headers': {…}})` | `run(…, model_settings={'extra_headers': {…}})` |
   | LangChain | `ChatOpenAI(base_url=…, default_headers={…})`, or tag via `base_url='…/t/<tag>'` | callback + httpx hook, below |

   Anything else: find where the SDK accepts a base URL and default headers, or an HTTP client it will use.

   Pydantic AI merges settings shallowly (model, then agent, then run), so a later `extra_headers` replaces an earlier one. Send the session and the tag together at the same level.

   LangGraph per-run sessions and per-node tags from one shared model: a callback reads the run metadata, an httpx request hook stamps the headers.

   ```python
   import contextvars, uuid
   from langchain_core.callbacks import BaseCallbackHandler

   _vessel = contextvars.ContextVar('vessel', default=None)

   class VesselCallback(BaseCallbackHandler):
       def on_chat_model_start(self, serialized, messages, *, metadata=None, **kwargs):
           md = metadata or {}
           _vessel.set({k: v for k, v in {'X-Vessel-Tags': md.get('langgraph_node'),
                                          'X-Vessel-Session': md.get('thread_id')}.items() if v})

   def _stamp(request):
       request.headers.update(_vessel.get() or {})

   llm = init_chat_model('ollama:llama3.2', base_url='http://127.0.0.1:4550',
                         client_kwargs={'event_hooks': {'request': [_stamp]}})
   tid = str(uuid.uuid4())
   graph.invoke(state, config={'configurable': {'thread_id': tid}, 'metadata': {'thread_id': tid},
                               'callbacks': [VesselCallback()]})
   ```

   `ChatOpenAI` takes the hook as `http_client=httpx.Client(event_hooks=…)`; async code needs an `async def` hook on the async client. Full example: https://github.com/spenceclark/Vessel/blob/main/docs/examples/langgraph.py

4. **Verify.** Send one request per agent through the project's own entry point, then `search_requests(limit: 10)` (or `curl -s 'http://127.0.0.1:4550/vessel/api/requests?limit=10'`) and `list_sessions`. Done when every agent has a captured row with the expected `backend` and `tags`, a 2xx `statusCode` and no `path_missing_v1`, and `list_sessions` shows each session name with a `requestCount` that matches. A changed base URL with no captured row is not done.

## Debug from captured traffic

### Tools

All read-only. Rows are newest first.

- `list_sessions(limit)` → `id`, `name`, `isCurrent`, `requestCount`, `lastRequestAt`. Map a session name to its numeric id here.
- `search_requests(query, backend, model, tag, status, format, sessionId, warnedOnly, limit, before)` → body-free rows and `nextBefore`; page older rows with `before: nextBefore`. With no `sessionId` it searches all history. `query` is literal full-text over prompt and response text. `status` is `ok` or `error`. `format` is one of `openai-chat`, `openai-responses`, `anthropic-messages`, `ollama-chat`, `ollama-generate`, `typesafe-systemone`, `raw`.
- `get_request(id, include, maxChars, offset)` → summary plus prompt and response windows. `include: "text"` (default) is the flattened conversation; `include: "raw"` is the decoded wire JSON, where request parameters (`max_tokens`, `temperature`, `tools`, `stream`, Ollama `options.num_ctx`) and structured tool calls live. Windows are 4,000 characters by default, 20,000 at most; a window's `note` names the next `offset`. `decodeTruncated` with no text means read `include: "raw"`.
- `get_stats(sessionId)` → totals, failures, average duration, TTFT and tok/s, token sums. With no `sessionId` it covers the current UI session only, which excludes `X-Vessel-Session` traffic; pass a numeric id or `"all"`.
- Resources `vessel://requests/{id}` and `vessel://sessions/{id}` attach a request or session to a prompt.

MCP returns no headers. Headers (auth redacted to scheme + last 4 characters) are in `GET /vessel/api/requests/{id}`.

When the `vessel` MCP server is unavailable, the same data is on the REST API: `/vessel/api/requests?q=&tag=&status=error&warned=1&session=<id>&limit=`, `/vessel/api/requests/{id}`, `/vessel/api/stats?session=current|all|<id>`, `/vessel/api/sessions`. To register MCP: `claude mcp add --transport http --scope user vessel http://127.0.0.1:4550/vessel/mcp` (the Vessel plugin registers only the default address); `mcp.enabled` must be true.

### Steps

1. **Scope.** A named run: `list_sessions`, then its `id`. Otherwise the agent's tag, a time window from the user, or the current session. Done when you have a `sessionId`, a `tag`, or both.
2. **Find the suspects.** `search_requests` with that scope plus `status: "error"`, then `warnedOnly: true`, then a `query` built from words in the bad output. Done when you hold the id of the failing request and, where one exists, the last good request from the same tag.
3. **Read them.** `get_request` as text, then raw for parameters and tool calls. Page every truncated window to its end before concluding something is absent from the prompt.
4. **Diagnose** against the signals below. Done when one captured fact explains the symptom.
5. **Report**: the request ids (the user opens them at `http://127.0.0.1:4550/vessel/`), the evidence quoted from the capture, the cause in the user's code as `file:line`, and the fix. Captures hold whatever the app sent, so quote only the lines that prove the point.

Replay, side-by-side Compare and scoring live in the Vessel UI. In rows, `replayOf`, `replayGroup` and `replayPatch` mark replays and the parameter patch each used; `score` is the user's 1-5 rating.

### Signals

| Signal | Meaning |
| --- | --- |
| Request absent from Vessel | The client bypasses Vessel; check its base URL. |
| 404 with `path_missing_v1` | The client's base URL lacks `/v1`. |
| 404, `error: unknown_backend` | `/b/<name>` or `X-Vessel-Backend` names a backend Vessel lacks. |
| 502/504, `proxy_error` | Vessel could not reach the backend: it is down, or its `baseUrl` is wrong. |
| 401/403, `http_error` | The client's own key; compare the redacted auth header's last 4 with the expected key. |
| `truncated_response`, `stopReason` `length`/`max_tokens` | Output hit the token limit; raise it. |
| `tool_call_in_text` | Tools were declared but the model wrote the call as plain text; check `tools` in the raw request and the model's tool support. |
| `repetitive_output` | Degenerate looping; check sampling parameters and context size. |
| `cold_load` | Ollama loaded the model for this request; the duration is load time. |
| `slow_ttft`, `slow_response` | Over the configured thresholds; compare `ttftMs` and `tokPerSec` with other requests on the same model. |
| `stream_incomplete`, `client_disconnect` | The stream was cut or the client gave up: a client timeout, cancellation or crash. |
| `tokens_estimated` | The backend reported no usage; counts are characters ÷ 4. |
| `parse_error` or format `raw` | Vessel did not recognise the shape; read it with `include: "raw"`. |
| `tokensIn` flat at a round number while the conversation grows | Likely Ollama truncating the prompt to `num_ctx`; check `options.num_ctx` in the raw request. |
| Healthy call, wrong answer | Read the prompt text: confirm the system prompt, tool results and retrieved context the agent needed are present, and watch `tokensIn` across the session for runaway context. |
