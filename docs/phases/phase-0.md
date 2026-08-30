# Phase 0 — Transparent Proxy Skeleton: Implementation Spec

> Expands Phase 0 of [plan.md](plan.md). Design authority is [architecture.md](architecture.md);
> this spec makes the concrete decisions that document deliberately left open.
>
> **Goal:** Vessel can sit in front of Ollama (and remote OpenAI/Anthropic) all day
> without the user noticing it exists. No capture, no DB, no UI — pass-through
> correctness only, proven by a verification harness.

## 0. Scope

**In:** project skeleton, config load/create, routing (default / path prefix / header),
header stripping, streaming pass-through, remote HTTPS backends, Vessel-generated error
responses, `/vessel/api/status`, verification harness, single-file publish smoke test.

**Out (explicitly):** body capture/tee, SQLite, format adapters, tags *semantics* (we
parse and strip tag prefixes/headers but do nothing with them), `injectStreamUsage`,
UI, SSE events, sessions.

---

## 1. Key implementation decisions

These are the micro-decisions the plan left open. Deviations found during implementation
go back into this file and, if architectural, into architecture.md §12.

### Implementation findings (recorded as the phase landed)

- **Host header must be nulled explicitly.** YARP's base `HttpTransformer` copies the
  client's `Host` through; `VesselTransformer` sets `proxyRequest.Headers.Host = null`
  after the base call so Host derives from the backend URI (T2 caught this — the D4.2
  "verify in the harness" instruction paid off).
- **Config models are classes, not records.** `System.Text.Json` rejects
  `[JsonExtensionData]` on types with a deserialization constructor, which records
  trip over. Plain mutable classes with the source-generated context work.
- **Tags are orthogonal to backend selection.** `/t/{tags}/…` with an
  `X-Vessel-Backend` header routes to the header's backend (the §3.2 rule order reads
  literally as "default backend" there, but tags don't *select* anything — the header
  is only ignored when a `/b/` prefix names the backend).
- **`not_found` error code added** for unrecognized `/vessel/*` paths — the reserved
  namespace is never proxied, and D5's marking convention (header + `source: "vessel"`)
  applies to that response too.
- **Test stack:** xunit v3 on .NET 10 requires the Microsoft.Testing.Platform `dotnet
  test` mode — opt-in lives in `global.json` (`"test": { "runner":
  "Microsoft.Testing.Platform" }`); `Microsoft.NET.Test.Sdk` is not referenced.
- **Trimming data (for the Phase 6 decision):** `PublishTrimmed=true` works today —
  18.3 MB vs 98.9 MB untrimmed on win-x64, zero trim warnings, publish smoke passes
  (first-run config, status, proxying, error paths). Left **off** in the csproj;
  `publish-smoke.ps1 -Trimmed` re-gathers the data any time.
- **Byte-identical vs real backends:** two generations are never byte-identical even
  direct-to-direct — Ollama stamps `created_at` and `*_duration` per call, OpenAI
  format stamps `id`/`created`. `verify.ps1` therefore masks exactly those volatile
  fields when raw bytes differ; the generated content and token counts must still
  match exactly (seed + temperature 0). `Content-Length` is checked for
  self-consistency per response rather than cross-compared in that case.

### D1 — YARP via `IHttpForwarder` (direct forwarding), not the ReverseProxy pipeline

Vessel's routing is custom (per-request backend selection from path/header/default), so
YARP's declarative routes/clusters config model buys nothing. Use the low-level
**direct forwarding** API: `services.AddHttpForwarder()` + one catch-all endpoint that
resolves the backend and calls `IHttpForwarder.SendAsync(...)`. This is YARP's documented
pattern for custom proxies and keeps all of YARP's streaming, header hygiene, and upgrade
handling.

### D2 — One shared `HttpMessageInvoker` for all backends

Created once at startup, per YARP direct-forwarding guidance:

```csharp
new HttpMessageInvoker(new SocketsHttpHandler
{
    UseProxy = false,
    AllowAutoRedirect = false,
    AutomaticDecompression = DecompressionMethods.None,   // forward-as-is: never decode
    UseCookies = false,
    EnableMultipleHttp2Connections = true,
    ActivityHeadersPropagator = null,                     // no traceparent injection
    ConnectTimeout = TimeSpan.FromSeconds(15),
});
```

`AutomaticDecompression = None` matters: if the client asked for gzip, the client gets
gzip. Vessel never re-encodes (capture will deal with encodings in Phase 1).

### D3 — Timeouts sized for LLM traffic

`ForwarderRequestConfig.ActivityTimeout` default is 100 s — a big prompt on a cold local
model can sit longer than that with **zero bytes moving** during prompt eval. Set
`ActivityTimeout` from config, default **30 minutes** (`timeouts.activitySeconds: 1800`).
HTTP version: `Version = HTTP/2`, `VersionPolicy = RequestVersionOrLower`.

### D4 — Kestrel limits

`MaxRequestBodySize = null` (unlimited — base64 images in prompts routinely exceed the
30 MB default; Vessel must not be the thing that rejects them). `KeepAliveTimeout` left
default. No response compression middleware, no response buffering middleware — nothing
between the forwarder and the wire.

### D5 — Vessel-generated errors are marked

When *Vessel itself* produces a response (unknown backend, upstream unreachable), it
must be distinguishable from a proxied backend error:

- Header `X-Vessel-Error: <code>` on the response.
- JSON body: `{ "error": { "source": "vessel", "code": "...", "message": "...", ... } }`.

Codes in Phase 0: `unknown_backend` (404 — includes `"backends": [...]` listing valid
names), `upstream_unreachable` (502), `upstream_timeout` (504). Map from YARP's
`ForwarderError` enum; client-disconnect errors produce **no** response (nobody's
listening) and are logged at Debug only.

> Note: architecture.md §3.2 already specifies 404 for an unknown backend, so no
> update was needed there — 404 (the named backend doesn't exist) with 502/504
> reserved for real upstream failures is what shipped.

### D6 — Config handling

Plain `System.Text.Json` (source-generated context — required for trimming anyway), not
`IConfiguration` binding. Load once at startup; hot-reload is a later phase. Unknown
JSON properties are **preserved on save** (`JsonExtensionData`) so a Phase-0 binary
doesn't destroy Phase-4 settings. Config path: `--config <path>` first; otherwise an
existing `vessel.json` next to the executable selects portable mode; otherwise Vessel
creates the platform `vessel-proxy` config directory on first run. This Phase 6 update
supersedes the original beside-the-exe-only rule. Malformed config → print error and exit
non-zero (never silently fall back to defaults over a typo).

First run (file absent): write the default config (Ollama backend, §9 of architecture.md)
and print:

```
Vessel listening on http://127.0.0.1:4550  →  default backend: ollama (http://localhost:11434)
Point your client at http://127.0.0.1:4550 — UI at http://127.0.0.1:4550/vessel/ (phase 3)
```

### D7 — Logging

Console logging, default level Warning for framework categories. One Information line at
startup (listen address + backends). Per-request logging only at Debug. Vessel should be
*silent* in normal operation.

---

## 2. Project skeleton

```
Vessel.sln
Directory.Build.props            # net10.0, nullable enable, implicit usings, warnings-as-errors
src/Vessel/
  Program.cs                     # host setup, Kestrel limits, DI, endpoint map
  Config/
    VesselConfig.cs              # records: VesselConfig, BackendConfig, TimeoutConfig
    ConfigLoader.cs              # load/create/save, JsonSerializerContext
  Proxy/
    BackendRegistry.cs           # name → resolved backend lookup (case-insensitive)
    RouteResolver.cs             # pure function: (path, headers) → RouteDecision
    VesselTransformer.cs         # HttpTransformer: strip X-Vessel-*, path rewrite
    ProxyHandler.cs              # catch-all endpoint: resolve → forward → map errors
  Api/
    StatusEndpoint.cs            # GET /vessel/api/status
tests/Vessel.Tests/
  RouteResolverTests.cs
  ConfigLoaderTests.cs
  ProxyIntegrationTests.cs       # in-proc Kestrel stub backend, see §5
verify/
  verify.ps1                     # byte-identical harness against a real backend, see §6
```

NuGet: `Yarp.ReverseProxy` (forwarder lives there), `xunit`, nothing else. Packages
pinned to latest stable at implementation time.

Prerequisite chore, first commit: `git init`, commit existing docs + editorconfig.

---

## 3. Routing

### 3.1 `RouteDecision`

`RouteResolver.Resolve(PathString path, IHeaderDictionary headers, BackendRegistry reg)`
is a **pure function** returning:

```csharp
record RouteDecision(
    BackendConfig? Backend,      // null → unknown backend name (error path)
    string? RequestedName,       // what the client asked for, for the error message
    PathString ForwardPath,      // original path minus /b/... and /t/... prefixes
    string[] Tags,               // parsed but unused in phase 0
    RouteSource Source);         // PathPrefix | Header | Default
```

### 3.2 Resolution rules (precedence order, per architecture §3.2)

1. Path starts with `/b/{name}` → backend `{name}`; continue parsing at the remainder.
2. Then optionally `/t/{tags}` (comma-separated) → tags; remainder is `ForwardPath`.
   `/t/{tags}` is also honored *without* a `/b/` prefix (default backend + tags).
3. No path prefix → `X-Vessel-Backend` header, if present.
4. Otherwise → default backend.

Edge cases (all unit-tested):

| Input | Decision |
|---|---|
| `/b/ollama/api/chat` | ollama, `/api/chat` |
| `/b/OLLAMA/api/chat` | backend names case-insensitive |
| `/b/nope/api/chat` | `Backend = null` → 404 `unknown_backend` |
| `/b/ollama` or `/b/ollama/` | ollama, `/` |
| `/b/` (no name) | 404 `unknown_backend`, name `""` |
| `/b/ollama/t/planner,run42/api/chat` | ollama, tags `[planner, run42]`, `/api/chat` |
| `/t/planner/v1/chat/completions` | default backend, tag, `/v1/chat/completions` |
| `/b/x/api` **and** `X-Vessel-Backend: y` | path wins (precedence), header ignored |
| `/vessel/...` | never reaches the resolver — reserved, mapped first |
| `/api/chat` (plain) | default backend, `Source = Default` |

Query strings pass through untouched in all cases.

### 3.3 Reserved namespace

`/vessel/{**}` is mapped before the catch-all and is never proxied. A backend path that
legitimately starts with `/vessel` cannot be reached via the default route — accepted
limitation; `/b/{name}/vessel/...` still works because the prefix is stripped. Document
in README later.

---

## 4. Forwarding

### 4.1 The catch-all endpoint

```
app.Map("/{**catchall}", ProxyHandler.Handle)   // after /vessel mappings
```

Handler flow:

1. `RouteResolver.Resolve(...)`.
2. `Backend == null` → write `unknown_backend` JSON (D5), done.
3. `forwarder.SendAsync(context, backend.BaseUrl, invoker, requestConfig, transformer)`.
4. Inspect returned `ForwarderError`; if an error occurred **and** the response hasn't
   started, write the mapped JSON error (D5). If the response already started (mid-stream
   upstream death), the connection is aborted — nothing else is possible; log at Debug.

### 4.2 `VesselTransformer` (extends `HttpTransformer`)

On request:
- Rewrite the destination path to `RouteDecision.ForwardPath` + original query.
- Remove every request header whose name starts with `X-Vessel-` (case-insensitive) —
  the only mutation Vessel makes, per architecture §3.4.
- Do **not** restore the original `Host` (YARP default: Host comes from the backend URI —
  required for TLS/SNI on api.openai.com / api.anthropic.com).
- Leave YARP's default `X-Forwarded-*` behavior as-is (defaults on `HttpTransformer.Default`
  add nothing for direct forwarding — verify in the harness that no unexpected headers
  appear; if any do, suppress them here).

On response: nothing. Response headers and body are untouched.

### 4.3 Streaming

`IHttpForwarder` streams both directions with no buffering. Phase-0 obligations are
purely *negative*: no middleware that buffers, no compression, no `context.Response.Body`
wrapping. The integration tests assert flush-through behavior (§5, T7).

---

## 5. Automated tests

Unit (pure, fast): every row of the §3.2 table; config round-trip incl. unknown-property
preservation; malformed-config rejection.

Integration — in-proc **stub backend** (second Kestrel on a random port) with endpoints:

- `/echo` — returns method, path, query, headers, body hash as JSON (proves forward-as-is).
- `/sse` — emits N SSE chunks on a timer, flushing each.
- `/ndjson` — same, NDJSON (Ollama shape).
- `/slow-headers` — waits before responding (timeout behavior).

| # | Assertion |
|---|---|
| T1 | Body bytes, method, path, query arrive at the stub unmodified (binary body with invalid-UTF8 bytes included) |
| T2 | All client headers arrive **except** `X-Vessel-*`; no unexpected Vessel/YARP-added headers beyond documented ones |
| T3 | Response status, headers, body return to the client unmodified |
| T4 | `/b/{name}` prefix routes to the right stub and is stripped from the forwarded path |
| T5 | `X-Vessel-Backend` routes; path prefix beats header when both present |
| T6 | Unknown backend → 404, `X-Vessel-Error: unknown_backend`, JSON lists valid backends |
| T7 | **Streaming is unbuffered**: with the stub emitting a chunk every 200 ms, the client observes inter-chunk gaps ≈ 200 ms — the first chunk arrives while later chunks are still unsent (assert on arrival timestamps, not just final body) |
| T8 | Stub dies mid-stream → client connection aborted (no fabricated clean ending) |
| T9 | Backend unreachable (closed port) → 502 `upstream_unreachable` |
| T10 | `/vessel/api/status` returns version, listen address, backend names + which is default |

T7 is the credibility test for the whole product — if it's flaky, fix the test harness,
never loosen the assertion.

---

## 6. Verification harness (real traffic)

`verify/verify.ps1` — runs against a **real** backend (Ollama by default), sending each
request twice — direct and via Vessel — and comparing:

- Status code and body **byte sequence** (streamed responses compared as the concatenated
  byte sequence — chunk *boundaries* are legitimately not preserved by re-chunking;
  byte content must be identical for non-sampling requests, so use `"seed"`/
  `temperature: 0` where the backend honors it, and fall back to comparing structure
  [chunk count ±, event fields present] where determinism isn't achievable).
- Response headers, minus a documented ignore-list (`Date`, `Server`, connection/hop-by-hop
  headers, `Transfer-Encoding`).
- First-byte latency delta (rough `vessel_overhead` preview; expect low single-digit ms).

Cases: Ollama native `/api/chat` streamed + non-streamed, Ollama `/v1/chat/completions`
SSE streamed + non-streamed, `curl -N` manual smoke, and (when keys are present, opt-in
flags) one OpenAI live and one Anthropic live call via `/b/openai/v1/...` /
`/b/anthropic/v1/messages`.

### Manual checklist (do these literally, once, before calling the phase done)

- [x] `$env:OLLAMA_HOST = "127.0.0.1:4550"; ollama run <model>` — interactive chat works,
      tokens visibly stream. *(verified non-interactively: `ollama run qwen2.5:1.5b
      "prompt"` through Vessel answers correctly; chunk-by-chunk streaming confirmed via
      `curl -N` NDJSON timestamps ~85 ms apart — worth one interactive session too)*
- [ ] OpenAI SDK (any script) with `base_url = http://127.0.0.1:4550/v1` against Ollama —
      streamed completion works. *(harness covers `/v1/chat/completions` streamed +
      non-streamed over raw HTTP; an actual SDK run is still pending)*
- [ ] Same script with `base_url = http://127.0.0.1:4550/b/openai/v1` + real key — works.
      *(pending keys; `verify.ps1 -OpenAI`)*
- [ ] Anthropic SDK with `base_url = http://127.0.0.1:4550/b/anthropic` + real key — works.
      *(pending keys; `verify.ps1 -Anthropic`)*
- [ ] Kill Ollama mid-stream — client errors promptly, Vessel stays healthy for the next
      request. *(covered in-proc by T8 against a stub; the literal kill-Ollama run is
      left for a moment when killing your Ollama is convenient)*
- [ ] Vessel under an agent tool (Cline/Aider/whatever you actually use) for one real task.

---

## 7. Single-file publish smoke test (do not defer)

Per the plan's risk table, prove packaging **now**:

```
dotnet publish src/Vessel -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

Run the produced exe from an empty directory: first-run config creation works, proxying
works, `/vessel/api/status` works. Add a CI-runnable script `verify/publish-smoke.ps1`.
Trimming (`PublishTrimmed`) is **attempted** here; if YARP or STJ source-gen trims badly,
record the finding and disable it with a comment — the decision point is Phase 6, but the
data is gathered now.

---

## 8. Acceptance criteria (phase gate)

1. All §5 tests green in CI-able form (`dotnet test`).
2. `verify.ps1` passes against a real Ollama for all non-live cases.
3. Manual checklist complete, including one real agent-tool session.
4. Publish smoke test passes on win-x64.
5. `vessel_overhead` preview from the harness is ≤ ~5 ms for local backends.
6. plan.md Phase 0 boxes ticked; any deviations reflected here and in architecture.md.
