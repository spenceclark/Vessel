# Phase 1 — Capture and Persistence: Implementation Spec

> Expands Phase 1 of [plan.md](plan.md). Design authority is [architecture.md](architecture.md);
> this spec makes the concrete decisions that document deliberately left open.
>
> **Goal:** everything that passes through the proxy is recorded, cheaply. A day of real
> traffic produces a `vessel.db` you can query by hand in a SQLite browser, the DB stays
> under its caps, and no plaintext key appears anywhere in it.

## 0. Scope

**In:** request/response tee streams with memory cap + `truncated` flag (§4.1), timings
on a monotonic clock (§4.2), header redaction at rest (§8), `Channel<CaptureRecord>` +
batched background writer (§6.1), SQLite schema v1 + migrations + WAL + zstd bodies
(§6.2), raw-fallback capture of everything (§5), retention caps + incremental vacuum
(§6.4), tests for tee fidelity / timings / redaction.

**Out (explicitly):** format adapters and payload sniffing (`format` is always `raw`
this phase), stream reassembly into `response_body` for streamed responses, warning
flags, token counts, sessions *semantics* (the table exists; `session_id` stays NULL
until Phase 3), FTS population (the virtual table exists; Phase 2 supplies the text,
Phase 4 wires search), SSE lifecycle events, UI, replay.

---

## 1. Key implementation decisions

Deviations found during implementation go back into this file and, if architectural,
into architecture.md §12.

### Implementation findings (recorded as the phase landed)

- **Redaction scheme detection must exclude cookie-shaped values.** Splitting at the
  first space would treat `sid=<secret>; Path=/` as scheme `sid=<secret>;` and preserve
  the secret verbatim. A leading token only counts as a scheme when it contains no
  `=`, `;`, or `,` (a plain RFC auth-scheme token). Caught by the C5 unit test; D6
  updated in place.
- **Size-cap deletion is progressive, ~1% of rows per iteration** (min 1), not a fixed
  chunk. A fixed chunk of 100 wiped the entire table (newest rows included) whenever
  the table held fewer than 100 rows — deleting *until* under the cap must never
  overshoot into recent history. Caught by C10; D11 updated in place.
- **`IncludeNativeLibrariesForSelfExtract=true` is required.** SQLite's `e_sqlite3` is
  a native library; without the flag it lands *beside* the exe and a bare single-file
  copy dies at startup with `DllNotFoundException`. Caught by the publish smoke test —
  exactly the "prove packaging now" risk the plan called out.
- **Trim data refresh (for the Phase 6 decision):** with Microsoft.Data.Sqlite 10.0.11
  and ZstdSharp.Port 0.8.8 added, `PublishTrimmed=true` still produces zero trim
  warnings and passes the publish smoke — 21.1 MB trimmed vs 101.8 MB untrimmed
  (win-x64). Trimming remains **off** in the csproj.
- **Live-harness observation:** `verify.ps1` against a real Ollama passes with capture
  on; warm first-byte deltas −2.6 to +9 ms, and the captured rows show
  `vessel_overhead_ms` of 0.09–0.41 ms warm (~26 ms on the very first request — JIT).

### D1 — Tee via stream wrappers installed in `ProxyHandler`

Phase 0's "no `context.Response.Body` wrapping" obligation is deliberately retired —
the tee *is* a body wrap; the obligation becomes "the wrap never withholds or reorders
a chunk".

- **Request**: `context.Request.Body` is replaced with a read-through tee. Bytes are
  appended to the capture buffer as YARP reads them upstream; the read result is never
  altered.
- **Response**: the `IHttpResponseBodyFeature` is replaced with a
  `StreamResponseBodyFeature` over a write-through tee (this covers both the `Stream`
  and `PipeWriter` write paths). Each chunk is written to the client **first**, then
  appended to the buffer; flushes pass straight through. No buffering, no back-pressure
  from capture.
- Tees are installed for every request that reaches the catch-all — including requests
  that end in a Vessel-generated error, so those responses are captured too.
  `/vessel/*` (Vessel's own API) is never captured.

### D2 — Capture buffers: plain capped `MemoryStream`

A `CaptureBuffer` wraps a `MemoryStream` with a byte cap (`capture.maxBodyMb`, default
32). Beyond the cap, appends are dropped and `Truncated` is set; proxied traffic is
unaffected. No pooling in Phase 1 — one contiguous buffer per body is fine at this
traffic level; revisit only if profiling ever says otherwise.

### D3 — Timing marks

One `Stopwatch` per request, started at handler entry (`started_at` is `DateTime.UtcNow`
at the same instant, stored ISO-8601 `"o"`):

| Mark | Taken |
|---|---|
| `overhead` | end of `VesselTransformer.TransformRequestAsync` — the moment the outbound request is fully prepared. `vessel_overhead_ms` = this mark. Null for requests that never reach the transformer (unknown backend, connect failure). |
| `request forwarded` | last read (data or EOF) returning from the request-body tee; falls back to the `overhead` mark for bodyless requests. |
| `first response byte` | entry of the first write on the response tee. |
| `duration_ms` | handler entry → capture record built (response body complete). |
| `ttft_ms` | `first response byte` − `request forwarded`, clamped ≥ 0. **Streamed responses only**, per §4.2; null otherwise. |

### D4 — `streamed` detection is wire-level

No parsing exists yet, so `streamed = 1` iff the response `Content-Type` is
`text/event-stream` or `application/x-ndjson` (case-insensitive, parameters ignored).
Phase 2's adapters may refine this; the heuristic covers all three v1 formats.

### D5 — Body columns in Phase 1

Capture stores exactly the wire bytes; nothing is decoded:

- Non-streamed response → `response_body` (wire bytes).
- Streamed response → `response_raw` (the raw chunk stream, per the §6.2 schema
  comment); `response_body` stays NULL until Phase 2 reassembly exists.
- `Content-Encoding` (gzip et al.) is **not** decoded at capture time — stored bytes
  match the wire, and the stored headers say how they're encoded. Phase 2 adapters
  decode when parsing. (In practice local backends don't compress; forward-as-is
  fidelity wins over browseability here.)
- Empty bodies → NULL, not empty BLOBs.

### D6 — Redaction: five headers + `Set-Cookie`, scheme + last 4

Applied to the stored copy only, on the request path, before the record is enqueued
(§8: plaintext never reaches the writer or DB). Redacted request headers:
`Authorization`, `Proxy-Authorization`, `X-Api-Key`, `Api-Key`, `Cookie`; response
headers: `Set-Cookie` (an addition over the §8 list — same secret class, recorded here).
Case-insensitive.

Format: `{scheme} …{last4}` when the value has a scheme prefix (`Bearer …-Ab4x`),
`…{last4}` otherwise. A leading token only counts as a scheme when it contains no
`=`, `;`, or `,` (a plain RFC auth-scheme token) — cookie values like
`sid=…; Path=/` never match. If the secret part is ≤ 8 chars, the last-4 tail is
omitted entirely (`Bearer …`) — too short to safely echo any of it.

Headers are stored as JSON objects `{name: [values]}`; the client's original header set
(including `X-Vessel-*`, which is stripped from the *forwarded* request only) is what's
stored.

### D7 — Record shape and the `path` column

`path` stores the **forward path + query string** (prefixes stripped) — the meaningful
API path; routing detail already lives in `backend` and `tags`. `tags` is a JSON array
from the route decision (header + `/t/` prefix, parsed since Phase 0). `format` is the
constant `raw` this phase. `error` stores a proxy-level failure code when Vessel itself
failed the request: the `VesselErrors` code when Vessel wrote the response
(`unknown_backend`, `upstream_unreachable`, `upstream_timeout`), `client_disconnect`
for client-side aborts, otherwise the YARP `ForwarderError` name.

### D8 — Writer: unbounded channel, 250 ms / 64-record batches

Singleton `Channel<CaptureRecord>` (unbounded, single reader). The writer is an
`IHostedService` registered before Kestrel's server service, so the DB is initialized
(fail-fast) before traffic is accepted, and shutdown drains: `StopAsync` completes the
channel, awaits the loop, flushes the final batch.

Batch rule per §6.1: flush when 64 records are collected or 250 ms have passed since
the first record of the batch arrived, whichever comes first. One transaction per batch.
zstd compression of bodies happens on the writer thread — never on the request path.

### D9 — SQLite specifics

- `Microsoft.Data.Sqlite`, one long-lived writer connection.
- Fresh DB: `PRAGMA auto_vacuum = INCREMENTAL` **before** any table exists (it's a
  no-op afterwards), then `journal_mode=WAL`, `synchronous=NORMAL`.
- Migrations via `PRAGMA user_version`: v1 creates the full §6.2 schema verbatim —
  `requests`, `sessions`, `requests_fts` (contentless), both indexes — even though
  sessions/FTS are unpopulated until later phases. Migration runner is a numbered list
  of scripts; opening a current DB is a no-op.
- DB file: `vessel.db` **next to the config file** (not the exe — matches the config's
  own location rule). Tests inject a temp path; `VesselApp.Build` takes it explicitly.

### D10 — zstd via ZstdSharp.Port

Managed port, no native binary to ship or trim (single-file publish stays trivial),
plenty fast for capture volumes. Compression level: default (3). All three body columns
are zstd-compressed unconditionally — no "too small to bother" carve-out, one code path.

### D11 — Retention after each batch

Per §6.4, enforced by the writer after each flushed batch, oldest-first by `id`:

- `retention.maxRequests` (default 10 000): one `DELETE` of everything older than the
  newest N.
- `retention.maxDbSizeMb` (default 500): while `page_count × page_size` exceeds the
  cap, delete the oldest ~1% of rows (min 1) and `PRAGMA incremental_vacuum`, until
  under the cap or the table is empty. Progressive so it converges quickly on a large
  overage without ever overshooting into recent rows.

No FTS delete maintenance this phase — nothing inserts into `requests_fts` yet; Phase 4
owns FTS consistency when it wires search.

### D12 — Config additions

```jsonc
{
  "retention": { "maxRequests": 10000, "maxDbSizeMb": 500 },
  "capture":   { "maxBodyMb": 32 }
}
```

All three values must be positive; validated like the Phase 0 settings (bad value →
error + non-zero exit). Existing configs without these sections get the defaults
(and keep them implicit until the file is next saved).

---

## 2. New/changed layout

```
src/Vessel/
  Capture/
    CaptureRecord.cs         # the record the writer consumes (raw bytes, pre-compression)
    CaptureBuffer.cs         # capped append buffer + Truncated flag
    CaptureContext.cs        # per-request state: stopwatch, marks, buffers → BuildRecord()
    RequestTeeStream.cs      # read-through tee
    ResponseTeeStream.cs     # write-through tee (client first, then buffer)
    HeaderRedactor.cs        # D6
    CaptureChannel.cs        # unbounded channel wrapper
    CaptureWriterService.cs  # hosted service: init DB, batch loop, retention, drain
  Storage/
    SqliteCaptureStore.cs    # connection, pragmas, migrations, batched insert, retention
    BodyCompression.cs       # zstd compress/decompress
tests/Vessel.Tests/
  CaptureIntegrationTests.cs # tee fidelity, timings, truncation, errors, tags
  RedactionTests.cs          # unit (redactor) + integration (nothing plaintext in the file)
  RetentionTests.cs          # both caps
  CaptureBufferTests.cs
```

`ProxyHandler` gains capture wiring (context creation, tees, record build in `finally`);
`VesselTransformer` stamps the overhead mark; `VesselApp.Build(config, dbPath)` — call
sites swept.

---

## 3. Automated tests

| # | Assertion |
|---|---|
| C1 | **Tee fidelity**: binary request/response bodies (invalid-UTF8 bytes included) arrive at stub and client unmodified *and* decompress from the DB byte-identical |
| C2 | **Streaming still unbuffered** with capture on: phase-0 T7 (chunk arrival timing) passes unchanged through the tees |
| C3 | **Truncation**: body over `maxBodyMb` → client/stub get full bytes; stored copy is capped and `truncated = 1` |
| C4 | **Timings sane**: `duration_ms` > 0; streamed → `ttft_ms` ≥ 0 and < duration; non-streamed → `ttft_ms` NULL; `vessel_overhead_ms` present and small for proxied requests |
| C5 | **Redaction**: every listed header, case-insensitive, multi-value; scheme + last-4 format; short secrets fully masked; other headers untouched; the secret string appears nowhere in the raw DB file bytes |
| C6 | **Raw fallback**: garbage POST to an unrecognized path → row with `format = raw`, bodies stored, silently |
| C7 | **Error rows**: unknown backend → row with `error = unknown_backend`, status 404; dead backend → `upstream_unreachable`, 502 |
| C8 | **Batching under load**: N concurrent requests all land as rows (no loss, no duplicate) |
| C9 | **Retention `maxRequests`**: cap 5, send 12 → ≤ 5 rows, newest kept |
| C10 | **Retention `maxDbSizeMb`**: 1 MB cap + incompressible bodies → DB file settles under the cap, oldest rows gone |
| C11 | **Tags/backend/path columns**: `/b/beta/t/planner/x?q=1` → `backend = beta`, `tags = ["planner"]`, `path = /x?q=1` |
| C12 | **Migrations**: fresh DB gets `user_version = 1`, WAL, incremental auto_vacuum; reopening is a no-op |
| C13 | Config: new sections round-trip, defaults applied when absent, non-positive values rejected |

C2 remains the credibility test — if the tee makes it flaky, fix the tee, never the
assertion. Readback in tests uses a separate read-only connection (WAL allows it) and
polls briefly for writer flush.

---

## 4. Acceptance criteria (phase gate)

1. All §3 tests green (`dotnet test`), phase-0 suite still green.
2. `verify.ps1` still passes against a real Ollama (capture on, bytes unchanged,
   overhead still low single-digit ms warm).
3. A real-traffic session produces a `vessel.db` browsable by hand: rows with
   timings, redacted headers, decompressible bodies.
4. No plaintext secret in the DB (C5 plus a manual spot-check).
5. Publish smoke still passes (new packages are trim/single-file clean).
6. plan.md Phase 1 boxes ticked; deviations recorded here.
