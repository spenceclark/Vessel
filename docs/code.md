# Vessel — Code Guide

Internal orientation to the codebase: how it is organized, what happens to a proxied
request end to end, where concurrency lives, what the APIs are, and what the UI is
built with. Ground truth is the code; this document describes it as it currently
stands (Phases 0–6 implemented). `docs/architecture.md` remains the design authority.

---

## 1. Project structure

One solution (`Vessel.sln`), one backend project, one frontend app.

```
Vessel.sln
src/Vessel/            # ASP.NET Core (.NET 10): proxy + capture + API + embedded UI
  Program.cs           # CLI args, config load, host start, startup messages
  VesselApp.cs         # Host construction shared by Program and the integration tests
  Config/              # vessel.json model, loader/validation, live-apply store
  Proxy/               # routing, backend registry, YARP forwarding, transformer
  Capture/             # tee streams, per-request context, channels, SSE hub, writer
  Formats/             # format detection + per-format adapters (background enrichment)
  Storage/             # SQLite write store, read store, compression
  Api/                 # /vessel/api endpoints, static UI, MCP gate, errors
  Mcp/                 # read-only MCP server (tools + endpoint mount)
frontend/              # React SPA, embedded into the binary at publish
tests/Vessel.Tests/    # xUnit: unit + integration (in-process host, stub backends)
verify/                # post-build verification scripts (verify.ps1, publish smoke, fixtures)
docs/                  # brief, architecture (design authority), plan, ui-spec, per-phase specs
```

### Backend namespaces

**Config/** — `VesselConfig` is the typed model of `vessel.json` (listen address,
backends, timeouts, retention, capture cap, warning thresholds, MCP switch, pricing
passthrough). `ConfigLoader` loads/creates/validates it; `ConfigPathResolver` decides
where config and DB live (`--config` > `vessel.json` beside the exe > platform config
dir). `ConfigStore` is the runtime seam: it holds the current config as a single
immutable `ConfigSnapshot(Config, Version)` reference, so a config read is atomic —
`PUT /vessel/api/config` validates with the same rules as startup, persists, and
swaps the snapshot (only `listen` needs a restart; the store records the actually
bound address for that check). `CaptureBudget` derives per-request wire/decode byte
caps from config.

**Proxy/** — `RouteResolver` is a pure function (path + headers + backends →
`RouteDecision`): `/b/{backend}` path prefix, then `X-Vessel-Backend` header, then the
default backend; `/t/{tags}` and `X-Vessel-Tags` add free-form tags. `ProxyHandler`
also captures an optional `X-Vessel-Session` exact name for writer-side assignment.
`BackendRegistry`
resolves a `ConfigSnapshot` into an immutable `BackendSet` (name → backend map +
default, one value so they can't disagree). `ProxyHandler` is the catch-all endpoint
(see §2). `VesselTransformer` is the YARP `HttpTransformer`: rewrites the destination
path, drops the client's Host (backend URI's host is used, needed for TLS/SNI), strips
every `X-Vessel-*` header, and stamps the `vessel_overhead_ms` mark. `ConcatStream`
supports the buffered-rewrite path; `FirstRunProbe` is the one-shot "is the default
backend reachable?" TCP connect on first run only.

**Capture/** — everything between the wire and the database:
- `CaptureContext` — per-request state: monotonic clock, request/response buffers
  (capped by `CaptureBuffer`), timing marks (overhead, request-forwarded, first/last
  response byte), error and provenance flags, and `BuildRecord` which produces the
  immutable `CaptureRecord` (headers already redacted by `HeaderRedactor`).
- `RequestTeeStream` / `ResponseTeeStream` — read-/write-through wrappers; capture
  never alters or withholds a byte.
- `CaptureChannel` — the unbounded `Channel<CaptureWork>` queue from request path to
  writer. `CaptureWork` is a union: captured requests plus control commands
  (`CreateSessionCommand`, `ClearCommand`, `DeleteSessionCommand`) that must execute on the single writer
  thread, each carrying a `TaskCompletionSource` for its HTTP caller.
- `CaptureWriterService` — hosted service; the single consumer. Initializes the DB
  before Kestrel accepts traffic, batches (≤64 records or 250 ms), enriches, inserts,
  enforces retention, and emits `completed` SSE frames. Resilient: a failed batch is
  dropped and the loop continues; five consecutive failures put the channel into a
  terminal stopped state (captures dropped at admission, commands fail fast, forwarding
  unaffected).
- `CaptureEvents` — the SSE hub (see §3).
- `RequestModelSnifferService` + `RequestModelSniffer` — dedicated background loop
  that parses the `model` field off a fully-read request body and publishes
  `request_ready`.
- `CurrentSession` — volatile holder of the Reset-driven active session id, read once
  per headerless request. Named requests do not change it.
- `BackendHealthTracker` — passive health (green/red dots) derived from captured
  outcomes; never generates backend traffic.

**Formats/** — `FormatEnricher.Enrich` is the entry point, called per record on the
writer thread: decode bodies (`BodyDecoder`, honoring Content-Encoding under the
configured budget), sniff format (`FormatDetector`: URL path first, then payload shape;
configured backend `type` is only a hint), run the adapter, then estimate missing
token counts (`TokenEstimator`, chars/4, flagged), compute tok/s (Ollama's exact
`eval_count/eval_duration` when present, else wire-span for streamed rows, else null),
and assemble warnings. `IFormatAdapter` implementations — `OpenAiChatAdapter`,
`OpenAiResponsesAdapter`, `AnthropicMessagesAdapter`, `OllamaAdapter` (chat and
generate modes) — fold SSE/NDJSON streams back into a reassembled message
(`SseParser`, `NdjsonParser`) and extract model, tokens (incl. cache read/write),
stop reason, flattened prompt/response text, and structured message/tool-call shapes.
Any adapter exception falls the row back to `raw` + `parse_error` with bytes intact.

**Storage/** — `SqliteCaptureStore` (the writer's store: migrations, batched inserts,
retention, sessions, clear) and `SqliteReadStore` (the API's read side: per-call pooled
read-only connections, WAL-concurrent with the writer; list/detail/facets/stats/
sessions queries plus backend-health seeds). Bodies are zstd-compressed at rest
(`BodyCompression`); full-text search is FTS5 over flattened prompt/response text.
`ICaptureStore` is the seam that lets writer resilience be tested without SQLite.

**Api/** — the `/vessel` control plane: endpoint handlers (see §4), `StaticUi` (serves
the embedded SPA, stripping the `vessel-ui/` resource prefix; placeholder when the
frontend wasn't built), `HostOriginGuard` (Host allowlist + same-origin check for
mutating API calls — scoped to `/vessel/*` only), and `VesselErrors` (the uniform
marked JSON error body).

**Mcp/** — `McpEndpoint` mounts the official SDK's Streamable HTTP handler at
`/vessel/mcp` behind the `mcp.enabled` kill-switch; `McpTools` defines the read-only
tools; `McpDtos` the wire shapes.

### Frontend layout

```
frontend/
  src/App.tsx               # single screen, no router: layout + selection state
  src/api/                  # typed client, query keys, SSE hook, live-history model
  src/components/           # StatsBar, FilterBar, RequestList/Row, DetailPane,
                            #   InFlightDetailPane, CompareView, ReplayDialog, banners,
                            #   MessageView, ToolCallCard, ConfigPanel, DataPanel...
  src/components/ui/        # shadcn-style primitives (button, badge, dialog, tabs,
                            #   popover, input) + Mark, ErrorState, PrettyJson
  src/render/               # per-format rendering of captured bodies into message
                            #   structures (openai, openaiResponses, anthropic, ollama)
  src/lib/                  # curl generation, formatting, warnings vocabulary, tags,
                            #   theme, cn() utility
```

Tests sit next to their components (`*.test.ts(x)`, vitest + Testing Library; several
are logic tests co-located with the component they cover).

### Build & packaging

`dotnet publish` runs `npm ci && npm run build` first (gated on the SDK's
`_IsPublishing` flag, so ordinary builds stay npm-free), embeds `frontend/dist` as
`vessel-ui/*` resources, and fails the publish if `index.html` didn't land. Release
artifacts are trimmed, self-contained single files (native libraries included so
e_sqlite3 travels inside the exe). `frontend/vite.config.ts` sets `base: '/vessel/'`
and proxies `/vessel/api` to a local Vessel during dev.

---

## 2. Lifetime of a proxied request

Everything not owned by Vessel ends up at the catch-all `ProxyHandler.Handle`. Walk a
request through:

1. **Arrival.** Kestrel accepts on the configured `listen` address. Middleware order:
   a control-plane guard applies **only** under `/vessel/*` (CSP header, Host
   allowlist, same-origin check for mutations, MCP enabled gate) — proxied routes are
   deliberately untouched. Well-known OAuth/openid paths and `/favicon.ico` are
   reserved and never proxied. Everything else reaches the catch-all.

2. **One config read.** `ProxyHandler` reads `ConfigStore.Snapshot` exactly once and
   uses that same reference for routing (`BackendRegistry.Resolve`) *and* this
   request's limits (activity timeout, capture cap) — a concurrent config PUT can
   never mix revision N's limits with revision N+1's backends.

3. **Capture context.** A `CaptureContext` is created: the Reset-driven session id is
   snapshotted from `CurrentSession`, and an optional trimmed `X-Vessel-Session` name
   is stamped alongside it. Two capped body buffers and a monotonic start timestamp
   follow. The context is stashed in `HttpContext.Items` so the transformer can find it.

4. **Response tee installed.** The `IHttpResponseBodyFeature` is replaced with a
   `StreamResponseBodyFeature` wrapping a `ResponseTeeStream` around the real stream.
   From here on, every byte YARP writes to the client is written *first*, then
   appended to the capture buffer. This covers both Stream and PipeWriter paths.

5. **Routing.** `RouteResolver.Resolve` strips `/b/{name}` and `/t/{tags}` prefixes,
   consults `X-Vessel-Backend`/`X-Vessel-Tags`, and falls back to the default backend.
   Unknown backend name → `capture.Error = unknown_backend` → a marked 404 JSON body
   naming the valid backends (still captured).

6. **Registration.** `capture.Register(...)` allocates the request's `seq` *and*
   inserts it into the hub's in-flight set in one critical section, publishing the
   `started` SSE frame. From this moment every exit path is inside the guarded
   `try/finally`, so a registered seq is guaranteed a terminal transition.

7. **Request body preparation.** Normally `context.Request.Body` is wrapped in a
   `RequestTeeStream`: as YARP reads the body upstream, bytes are appended to the
   capture buffer; the last read stamps `request_forwarded_ms` (the TTFT baseline) and
   a genuine EOF schedules the model-sniff. The one opt-in exception: for a backend
   with `injectStreamUsage` and a `/chat/completions` path, the body is buffered up to
   the cap first; if it is a streamed chat request without `stream_options`,
   `include_usage: true` is injected into the *forwarded* copy while the stored copy
   keeps the client's original bytes. Over-cap or non-qualifying bodies forward
   unmodified. A client disconnect during this read marks the record
   `client_disconnect` and falls through to the finalizer.

8. **Forwarding.** `_forwarder.SendAsync` (YARP direct forwarding, one shared
   `HttpMessageInvoker` — no proxy, no redirects, no auto-decompression, no cookies)
   streams the request to the backend. `VesselTransformer` rewrites the path, clears
   the Host header (so the backend URI's host applies), strips `X-Vessel-*` headers
   from request and content, and stamps `vessel_overhead_ms` — everything before this
   point is Vessel's measured per-request cost. `Authorization`/`x-api-key` pass
   through untouched; Vessel holds no keys.

9. **Response streaming back.** Backend response chunks flow through the response
   tee: write to client → stamp last-byte mark → append to buffer. The first write
   also stamps TTFT and, for streamed content types, publishes the live `first_token`
   SSE event. The client sees the stream unbuffered; capture never delays a byte.

10. **Forwarder errors.** If YARP reports an error: mid-response failures just mark
    the record (the client connection is already aborted); client-side failures mark
    `client_disconnect`; otherwise Vessel authors a marked 502/504 JSON body and flags
    `ResponseAuthoredByVessel` so enrichment won't parse Vessel's own error as a
    completion.

11. **Finalizer (the `finally`).** If the request body was never read (error paths),
    it is drained into the tee so failed rows still carry model + prompt text. Then
    `capture.BuildRecord(...)` produces the `CaptureRecord`: timings, streamed flag
    (SSE/NDJSON content type), redacted request/response headers (secrets become
    scheme + last 4), both body buffers (streamed responses go to `response_raw`,
    non-streamed to `response_body`), truncation and provenance flags. The record is
    handed to `BackendHealthTracker.Observe` and enqueued on `CaptureChannel` —
    fire-and-forget. If the channel is stopped (writer gave up), `completed{row:null}`
    is published here instead, so the seq still leaves the active set.

12. **Background writer.** The writer batches the record with others (≤64 or 250 ms,
    FIFO — a clear/session command observes every capture queued ahead of it), resolves
    any named-session selector by exact lookup-or-create on this writer (without changing
`CurrentSession`), runs enrichment (detection → adapter → estimation → warnings →
    tok/s; reassembled response for streams), inserts the batch in one transaction, enforces retention
    caps, and publishes one `completed` SSE frame per row carrying the real DB id and
    a `Summary`.

    Retention and clear also prune empty non-current session markers; explicit session
    deletion removes one marker plus its request/FTS rows in a transaction. The current marker
    is retained even with zero rows so headerless traffic always has a valid destination.

13. **Client side.** The HTTP response finished back in step 9/10; everything after
    was off the request path. The UI saw the request live through
    `started`/`request_ready`/`first_token`, then `completed` replaces the in-flight
    row with the persisted one (spliced into the first list page's cache).

Replay rides this same pipeline: `ReplayExecutor` posts the stored body to Vessel's
own `/b/{backend}/…` route with an `X-Vessel-Replay-Of` header and `X-Vessel-Tags`,
so the replayed request is captured, enriched, and linked (`replay_of`) exactly like
any other traffic.

---

## 3. Parallel & async processing

The system is async I/O end to end (Kestrel, YARP, SQLite via `Microsoft.Data.Sqlite`).
The interesting structure:

**Two background consumers, two channels.**
- `CaptureChannel` (unbounded, single reader) carries captured requests plus control
  commands to `CaptureWriterService`'s single long-running task. Batching, enrichment,
  and all SQLite writes happen on this one thread; commands run FIFO between capture
  runs, which is what makes "clear everything up to now" mean what the user asked.
- `RequestModelSnifferService` mirrors the shape (unbounded channel, one loop) for the
  cheap model-parse jobs that feed `request_ready`. It is a dedicated loop rather than
  `Task.Run` per request so the parse reliably lands before `first_token` even under
  thread-pool pressure.

Both loops never throw out of a single item's handling; the writer additionally
tolerates transient batch failures (drop, log, continue) and only after five
consecutive failures enters a terminal state: `CaptureChannel.Stop` closes admission,
in-flight captures are dropped at the door, queued commands fail with
`CaptureStoppedException`, and the drain path gives every raced-in item a terminal
transition. Forwarding is never blocked by capture health.

**The SSE hub (`CaptureEvents`).** One lock (`_publishLock`) guards the publish-id
counter, the fan-out, and the in-flight descriptor map — so ids are allocated and
enqueued in order, and `GetActiveRequests()` returns an in-flight set together with
the log position it is true as of (the recovery contract). Each subscriber gets a
bounded (256) drop-oldest channel: a stalled browser can never back-pressure the
request path; the monotonic `id:` on each frame is what makes a drop detectable.
Publishing serializes JSON outside the lock and skips it entirely with zero
subscribers. `EventsEndpoint` holds one SSE connection per open UI tab, writes a
`hello` (run id) straight to the wire before any hub frame, and heartbeats a comment
every 15 s.

**Config as an immutable snapshot.** `ConfigStore` publishes
`ConfigSnapshot(Config, Version)` behind a single volatile reference. Derived caches
key on the snapshot *reference*: `BackendRegistry.Resolve(snapshot)` builds a
`BackendSet` for exactly the snapshot asked for (never "newest by lock time");
`FormatEnricher` rebuilds its backend-type map and thresholds when the reference
changes; retention re-reads caps every batch. This makes a PUT racing a request
unrepresentable — there is no way to observe a version without the config it labels.

**Other concurrency points.** Replays are dispatched fire-and-forget, gated by a
`SemaphoreSlim` (max 4 concurrent). SQLite is single-writer (one non-pooled write
connection) + WAL, so `SqliteReadStore`'s pooled read-only connections run
concurrently. `CurrentSession` is an `Interlocked`-exchanged id. `FirstRunProbe` is a
single one-shot TCP connect at startup, deliberately not a background health check.

**In the UI.** SSE frames are queued and applied coalesced (~100 ms window, one state
update per flush — the per-frame path measurably stalled the main thread under 10k
bursts). Gap detection (id jump) and reconnects trigger snapshot reconciliation: fetch
`GET /active`, discard everything held at or below its `logPosition`, rebuild in-flight
rows from its descriptors, refetch lists/stats/facets with the outstanding fetch
cancelled first (last-started fetch wins). A `hello` run-id change means a restart and
drops all client-side seqs/positions wholesale. Elapsed-time animation uses a per-
consumer 250 ms `useNowTick` that turns off when nothing is in flight.

---

## 4. Query and utility APIs

All under `/vessel/` (can never collide with proxied `/v1/…` or `/api/…` paths).
Errors are uniform: JSON `{ "error": { "code", "message" } }` plus an `X-Vessel-Error`
marking (e.g. `not_found`, `invalid_request`, `forbidden_host`, `upstream_unreachable`).

| Route | Purpose |
|---|---|
| `GET /vessel/` | Embedded SPA (`StaticUi`; placeholder when no frontend was built) |
| `GET /vessel/api/requests` | Paged, cursor-based list (`limit` ≤ 500, `before` id). Filters combine: `q` (FTS5 over prompt/response text, sanitized), `backend`, `model`, `format`, `tag` (exact element), `status=ok\|error`, `warned=1`, `session` |
| `GET /vessel/api/requests/{id}` | Full detail — headers, decompressed bodies, reassembled/derived fields |
| `GET /vessel/api/requests/{id}/replays` | Direct replay children (Compare entry point) |
| `POST /vessel/api/requests/{id}/replay` | Re-send captured request; body may override `backend`/`model`; runs through the normal proxy pipeline, result linked via `replay_of` |
| `DELETE /vessel/api/requests` | Clear: `scope=all` or `before={ISO timestamp}`; runs on the writer thread; ack count is UX only |
| `GET /vessel/api/requests/facets` | Distinct backend/model/tag/format values for the filter bar |
| `GET /vessel/api/stats?session=` | Totals, failures, avg latency/tok/s/TTFT, token sums; `session` = id, `current`, or `all` |
| `GET /vessel/api/sessions` / `POST` | List sessions newest-first with name/current/count/last activity / reset (create marker + activate) |
| `DELETE /vessel/api/sessions/{id}` | Delete a non-current session marker and all its request/FTS rows atomically on the writer |
| `GET/PUT /vessel/api/config` | `{ config, restartRequired }` / apply (validates, persists, live-swaps snapshot; `listen` needs restart) |
| `GET /vessel/api/events` | SSE lifecycle feed: `hello`, `started`, `request_ready`, `first_token`, `completed`, `cleared` |
| `GET /vessel/api/active` | Recovery snapshot `{ active, logPosition, serverRunId }` |
| `GET /vessel/api/status` | Version, effective listen address, per-backend passive health, `mcp.enabled`, first-run setup state |
| `POST /vessel/mcp` | MCP Streamable HTTP (read-only), gated by `mcp.enabled` |

(`docs/architecture.md` §7 also lists `GET /vessel/api/ollama/ps`; that endpoint is
not mapped in code yet — it belongs to the Ollama-panel work still ahead. The UI's
Ollama-panel references are likewise future-phase.)

**Read-side semantics** (`SqliteReadStore`): every query is indexed — id cursor for
pagination, session index for scoping — and no query scans bodies. FTS queries are
sanitized so hostile input can't throw a syntax error, and the FTS join only happens
when the query sanitizes to something (rows with no flattened text — raw fallback —
would otherwise silently vanish from unfiltered lists). Detail bodies are decoded for
display under the same byte budget used at capture.

**MCP tools** (read-only, `McpTools`): `search_requests` (same filter semantics as the
history list, compact body-free rows, `nextBefore` cursor), `get_request` (windowed
text/raw bodies, 4k default/20k max chars, paging offset), `get_stats`, and
`list_sessions`. The server shares the control plane's Host guard and has no
additional auth — `/vessel/mcp` can read your captured prompts.

**Utility endpoints beyond the table:** `/.well-known/oauth-*` and
`/.well-known/openid-configuration` are answered with a marked 404 (reserved control
plane; MCP auth discovery probes hit them), and `/favicon.ico` serves the embedded
SVG. Copy-as-curl is generated client-side from the detail payload; there is
deliberately no server-side curl endpoint, and replay auth comes from environment
variables named per-backend (`authEnv`) — no secret is stored in config or DB.

---

## 5. UI framework

**Stack:** React 19 + TypeScript, built with Vite 8, styled with Tailwind CSS 4,
components in the shadcn/ui style (cva + tailwind-merge + clsx primitives in
`components/ui`), icons via lucide-react, `react-markdown` (+ GFM) for rendered
message text. Fonts ship as `@fontsource-variable` packages (Inter UI, JetBrains Mono
for code). Tests: vitest + Testing Library; lint: oxlint. No router — the product is
one screen.

**State and data:**
- **TanStack Query v5** owns all REST state. Query keys are centralized in
  `api/queryKeys.ts` (`['requests', scope, filters]` infinite list, `['request', id]`
  detail, `['sessions']` (also refreshed when a completion reveals an unknown named
  session id), `['status']` with a 5 s poll shared by StatsBar and the
  banners). The list is an infinite query keyed by the `before` cursor.
- **SSE** via `api/useEvents.ts`: a single `EventSource` to `/vessel/api/events`, gap
  detection on the `id:` watermark, `hello`-driven restart detection; handlers reach it
  through a ref so the subscription never reconnects on re-render.
- **`api/useLiveHistory.ts`** is the one reconciliation model (in-flight map,
  completion merging into the list cache, snapshot recovery, clear handling). App.tsx
  consumes it and supplies scope/filters/selection; `RequestList` only renders.
- **TanStack Virtual** virtualizes the history list (in-flight rows pinned above the
  loaded pages with a live timer; completions splice into the first page's cache when
  no filter beyond session scope is active, otherwise a "new requests" pill appears).

**Selection** is a small union: a completed row (DB id), an in-flight request (SSE
seq), or a compare pair. On `completed`, an in-flight selection hands over to the real
row id in place. Any clear evicts every cached `['request', *]` detail and clears the
selection if the clear reached it (guarding against SQLite id reuse).

**Rendering captured bodies:** `src/render/` normalizes OpenAI chat, OpenAI
Responses, Anthropic, and Ollama payloads into a shared message shape
(`MessageView` renders roles, `ToolCallCard` renders tool calls as readable cards,
images decode to `data:` previews). Two hard rules there: captured content never
produces a live `src`/`href` (defense in depth behind the CSP served on `/vessel/*`),
and rendering failures are contained by `RenderErrorBoundary` per pane rather than
taking down the app.

**Chrome:** `StatsBar` (selected-session totals + searchable bounded session picker +
count-confirmed single-session deletion + reset),
`FilterBar` (text + facet filters),
`DetailPane`/`InFlightDetailPane` (metrics incl. TTFT and Vessel overhead, headers,
request/response views with raw and raw-stream toggles, replay dialog), `CompareView`
(side-by-side diff), `ConfigPanel`/`ThemePanel`/`DataPanel`, plus `BindAddressBanner`,
`CaptureHealthBanner`, and `DecodeTruncatedNotice`. `DataPanel` owns typed-confirmation bulk
session deletion (multi-select, counts, current disabled) alongside clear-all/before. Theme (light/dark/system) is
initialized pre-paint by `public/theme-init.js` to avoid a flash.
