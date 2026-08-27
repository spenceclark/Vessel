# Phase 1 — Implementation Report

> Status: **complete**.
> Spec: [phase-1.md](phase-1.md) · Plan: [plan.md](plan.md) · Design authority: [architecture.md](architecture.md)

## What was built

Capture and persistence, per the spec's §2 layout:

| Piece | File | Notes |
|---|---|---|
| Capture record | `src/Vessel/Capture/CaptureRecord.cs` | Raw wire bytes + redacted header JSON; compression deferred to the writer |
| Capped buffer | `src/Vessel/Capture/CaptureBuffer.cs` | `capture.maxBodyMb` cap (default 32), `Truncated` flag; stored copy only, never the traffic |
| Per-request state | `src/Vessel/Capture/CaptureContext.cs` | One `Stopwatch`, timing marks (D3), wire-level streamed heuristic (D4), `BuildRecord` with redaction on the request path |
| Request tee | `src/Vessel/Capture/RequestTeeStream.cs` | Read-through; last read stamps the TTFT baseline |
| Response tee | `src/Vessel/Capture/ResponseTeeStream.cs` | Client-first write-through, flushes pass straight through; installed via `StreamResponseBodyFeature` (covers Stream + PipeWriter paths) |
| Redaction | `src/Vessel/Capture/HeaderRedactor.cs` | Authorization, Proxy-Authorization, X-Api-Key, Api-Key, Cookie (+ Set-Cookie), scheme + last 4 (D6) |
| Channel | `src/Vessel/Capture/CaptureChannel.cs` | Unbounded, single reader; fire-and-forget enqueue |
| Writer | `src/Vessel/Capture/CaptureWriterService.cs` | Hosted service ahead of Kestrel (fail-fast DB init); 64-record / 250 ms batches; drains on shutdown |
| Store | `src/Vessel/Storage/SqliteCaptureStore.cs` | WAL + `synchronous=NORMAL` + incremental auto_vacuum; `user_version` migrations (v1 = full §6.2 schema incl. sessions + FTS); batched transactional insert; both retention caps |
| Compression | `src/Vessel/Storage/BodyCompression.cs` | zstd via ZstdSharp.Port (managed — no native lib to ship) |
| Proxy wiring | `src/Vessel/Proxy/ProxyHandler.cs` | Tees installed at handler entry; record enqueued in `finally` — every request that reaches the catch-all lands a row, error paths included |
| Overhead mark | `src/Vessel/Proxy/VesselTransformer.cs` | Stamps `vessel_overhead_ms` when the outbound request is fully prepared |
| Config | `src/Vessel/Config/*` | `retention { maxRequests, maxDbSizeMb }`, `capture { maxBodyMb }` + validation |

`VesselApp.Build(config, dbPath)` — DB lives next to the config file; call sites swept
(Program, test fixtures). New packages: Microsoft.Data.Sqlite 10.0.11, ZstdSharp.Port 0.8.8.

## Verification results

### Automated tests — 71/71 pass (`dotnet test`; 45 phase-0 + 26 new)

- **C1** tee fidelity: binary bodies (invalid-UTF8 included) unmodified on the wire *and*
  byte-identical after decompression from the DB.
- **C2** (= phase-0 T7): chunk-arrival-timing streaming test passes unchanged through the
  tees — the credibility test did not need loosening.
- **C3** truncation: 1.5 MB body through a 1 MB cap → wire intact, stored copy capped,
  `truncated = 1`; both directions.
- **C4** timings: streamed → `ttft_ms` present and ≪ duration; non-streamed → NULL;
  overhead present and small. Streamed rows store `response_raw`, non-streamed
  `response_body`.
- **C5** redaction: all six headers, case-insensitive, multi-value, short-secret full
  masking; the plaintext secret appears nowhere in the raw bytes of the DB file or WAL.
- **C6/C7** raw fallback + error rows (`unknown_backend` 404, `upstream_unreachable` 502,
  with the Vessel error body itself captured).
- **C8** 100 concurrent requests → 100 rows, no loss, no duplicates.
- **C9/C10** both retention caps; **C11** backend/tags/path columns; **C12** migrations
  and pragmas; **C13** config round-trip + validation.

### Real-traffic harness (`verify.ps1`, local Ollama, `qwen2.5:1.5b`, capture ON)

All four cases pass — bodies identical after masking the same volatile fields as phase 0:

| Case | First-byte delta (Vessel − direct) |
|---|---|
| `/api/chat` non-streamed | −3 073 ms (model prompt-cache variance, not Vessel) |
| `/api/chat` NDJSON streamed | **+9.0 ms** (first request through a cold exe — JIT) |
| `/v1/chat/completions` non-streamed | **−2.6 ms** |
| `/v1/chat/completions` SSE streamed | **−2.6 ms** |

The resulting `vessel.db`, queried by hand: 4 rows with correct method/path/backend,
`streamed` flags, `ttft_ms` only on streamed rows, `vessel_overhead_ms` **0.09–0.41 ms
warm** (25.96 ms on the very first request — JIT warmup), and zstd bodies that
decompress to the exact NDJSON/SSE wire streams.

### Publish smoke (win-x64, self-contained, single file)

| Configuration | Size | Result |
|---|---|---|
| Untrimmed | 101.8 MB | all checks pass |
| `PublishTrimmed=true` | 21.1 MB | all checks pass, **zero trim warnings** |

The new packages required `IncludeNativeLibrariesForSelfExtract=true` (see findings);
trimming remains off pending the Phase 6 decision.

## Deviations and findings

Recorded in place in [phase-1.md](phase-1.md) §1; summary:

1. **Redaction scheme detection**: a leading token counts as a scheme only when it has
   no `=`/`;`/`,` — naive space-splitting preserved cookie secrets (`sid=…;` as
   "scheme"). Caught by C5.
2. **Size-cap deletion is progressive (~1%/iteration, min 1)**: a fixed 100-row chunk
   wiped small tables entirely, newest rows included. Caught by C10.
3. **`IncludeNativeLibrariesForSelfExtract=true`**: SQLite's native `e_sqlite3`
   otherwise lands beside the exe and a bare single-file copy dies at startup. Caught
   by the publish smoke — the plan's "prove packaging now" risk paying off again.
4. Phase 0's "no response-body wrapping" obligation is retired by design — the tee is
   a wrap; T7 proves it still never buffers.

## Acceptance criteria status

| # | Criterion | Status |
|---|---|---|
| 1 | §3 tests green, phase-0 suite still green | ✅ 71/71 |
| 2 | `verify.ps1` passes vs real Ollama with capture on, overhead low | ✅ (−2.6 to +9 ms; 0.09–0.41 ms measured overhead warm) |
| 3 | Real traffic → browsable `vessel.db` | ✅ (queried by hand, bodies decompress) |
| 4 | No plaintext secret in the DB | ✅ (C5 file-byte scan + hand check) |
| 5 | Publish smoke passes with new packages | ✅ (untrimmed + trimmed, zero trim warnings) |
| 6 | plan.md boxes ticked, deviations recorded | ✅ |

A day of real daily-drive traffic (the "done when" gate's soak) is the remaining
human-at-the-keyboard item, as in phase 0.
