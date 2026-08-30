# Phase 0 — Implementation Report

> Status: **complete** (pending the manual live-key / agent-tool checklist items below).
> Spec: [phase-0.md](phase-0.md) · Plan: [plan.md](plan.md) · Design authority: [architecture.md](architecture.md)

## What was built

The transparent proxy skeleton, exactly per the spec's §2 layout:

| Piece | File | Notes |
|---|---|---|
| Host setup | `src/Vessel/Program.cs` | `--config <path>` arg, first-run banner, single Information startup log line |
| App composition | `src/Vessel/VesselApp.cs` | Shared by Program and integration tests; Kestrel `MaxRequestBodySize = null`, framework logging at Warning |
| Config models | `src/Vessel/Config/VesselConfig.cs` | STJ source-generated context; unknown JSON properties preserved via `JsonExtensionData` |
| Config load/create | `src/Vessel/Config/ConfigLoader.cs` | Create-on-first-run (Ollama default), malformed config → error + non-zero exit, validation of backends/listen/timeouts |
| Backend lookup | `src/Vessel/Proxy/BackendRegistry.cs` | Case-insensitive name → backend; base URLs normalized |
| Routing | `src/Vessel/Proxy/RouteResolver.cs` | Pure function; `/b/{name}`, `/t/{tags}`, `X-Vessel-Backend`, default — full §3.2 precedence table |
| Transformer | `src/Vessel/Proxy/VesselTransformer.cs` | Path rewrite + `X-Vessel-*` strip; Host derived from backend URI; responses untouched |
| Forwarding | `src/Vessel/Proxy/ProxyHandler.cs` | YARP `IHttpForwarder` direct forwarding (D1), shared invoker (D2), 30-min activity timeout (D3), error mapping (D5) |
| Errors | `src/Vessel/Api/VesselErrors.cs` | `X-Vessel-Error` header + `{"error":{"source":"vessel",...}}` body |
| Status | `src/Vessel/Api/StatusEndpoint.cs` | `GET /vessel/api/status` — version, listen address, backends + default |
| Tests | `tests/Vessel.Tests/` | Route/config unit tests + in-proc integration tests against real Kestrel stub backends |
| Harness | `verify/verify.ps1` | Direct-vs-Vessel comparison against a real backend |
| Publish smoke | `verify/publish-smoke.ps1` | Single-file publish, run from empty dir, proxy + error-path checks |

## Verification results

### Automated tests — 45/45 pass (`dotnet test`)

- Every row of the spec's §3.2 routing table, plus header-tag merging and the `/b`
  (no-trailing-slash) edge case.
- Config: first-run creation, unknown-property round-trip survival, malformed JSON,
  bad `defaultBackend`/`baseUrl`/`listen` rejection.
- Integration T1–T10 per spec §5, plus two extras:
  - **T11**: activity timeout → `504 upstream_timeout`.
  - **T12**: `/vessel/*` reserved namespace is never proxied.
- **T7 (the credibility test)** asserts on chunk *arrival timestamps* for both SSE and
  NDJSON: first chunk arrives before later chunks are sent, arrivals spread over the
  stream duration. Passed without loosening.

### Real-traffic harness (`verify.ps1`, local Ollama, `qwen2.5:1.5b`)

All four cases pass:

| Case | Body | First-byte delta (Vessel − direct) |
|---|---|---|
| `/api/chat` non-streamed | identical after masking volatile fields | −2 056 ms (model prompt-cache variance, not Vessel) |
| `/api/chat` NDJSON streamed | identical after masking volatile fields | **−0.9 ms** |
| `/v1/chat/completions` non-streamed | identical after masking volatile fields | **+4.6 ms** |
| `/v1/chat/completions` SSE streamed | identical after masking volatile fields | **+0.3 ms** |

Warm-path overhead is within the ≤ ~5 ms acceptance gate. "Masking volatile fields"
means exactly `id`/`created`/`created_at`/`*_duration`/`system_fingerprint` — two
generations are never byte-identical even direct-to-direct because the backend stamps
those per call; generated content and token counts matched exactly (seed 42,
temperature 0).

### Drop-in checks

- `OLLAMA_HOST=127.0.0.1:4550` + `ollama run qwen2.5:1.5b "prompt"` → answers
  correctly through Vessel.
- `curl -N` on `/api/chat` streamed → NDJSON chunks arrive progressively
  (~85 ms apart by their `created_at` stamps).

### Publish smoke (win-x64, self-contained, single file)

| Configuration | Size | Result |
|---|---|---|
| Untrimmed | 98.9 MB | all checks pass |
| `PublishTrimmed=true` | 18.3 MB | all checks pass, **zero trim warnings** |

Checks: first-run config creation next to the exe, `/vessel/api/status`, proxying
through an `HttpListener` stub (path + body intact), unknown-backend 404. Trimming is
left **off** in the csproj; the data is recorded for the Phase 6 decision
(`verify/publish-smoke.ps1 -Trimmed` re-gathers it any time).

## Deviations and findings

Recorded in place in [phase-0.md](phase-0.md) §1 ("Implementation findings"); summary:

1. **Host header**: YARP's base `HttpTransformer` copies the client's `Host`;
   `VesselTransformer` nulls it so Host derives from the backend URI (required for
   TLS/SNI on remote APIs). Caught by T2.
2. **Config models are classes**: STJ rejects `[JsonExtensionData]` on records.
3. **Tags are orthogonal to backend selection**: `/t/…` + `X-Vessel-Backend` header →
   the header still selects the backend. Judgment call, flagged in the spec — easy to
   flip if the stricter reading of §3.2 was intended.
4. **`not_found` error code** added for unrecognized `/vessel/*` paths, following the
   D5 marking convention.
5. **Test stack**: xunit v3 on .NET 10 SDK requires Microsoft.Testing.Platform mode —
   opted in via `global.json`; run with `dotnet test` (or `dotnet test --solution Vessel.sln`).
6. **architecture.md §3.2 needed no update** — it already specified 404 for unknown
   backends (the spec's D5 note assumed it said 502).
7. **PowerShell 5.1 note**: the verify scripts are kept pure ASCII — Windows PowerShell
   misreads BOM-less UTF-8 as ANSI, and a mangled em dash can silently corrupt parsing
   (a Unicode right-double-quote acts as a string terminator).

## Acceptance criteria status

| # | Criterion | Status |
|---|---|---|
| 1 | §5 tests green, CI-able (`dotnet test`) | ✅ 45/45 |
| 2 | `verify.ps1` passes vs real Ollama (non-live cases) | ✅ |
| 3 | Manual checklist incl. agent-tool session | ⬜ partially — see below |
| 4 | Publish smoke on win-x64 | ✅ (untrimmed + trimmed) |
| 5 | Overhead preview ≤ ~5 ms local | ✅ (−0.9 to +4.6 ms warm) |
| 6 | plan.md boxes ticked, deviations recorded | ✅ |

**Remaining manual items** (need live keys / a human at the keyboard):

- One interactive `ollama run` session via `OLLAMA_HOST`.
- OpenAI SDK against Ollama (`base_url=…/v1`) and against live OpenAI
  (`…/b/openai/v1` + key); Anthropic SDK via `…/b/anthropic` + key
  (`verify.ps1 -OpenAI -Anthropic` covers the raw-HTTP versions).
- Kill Ollama mid-stream (covered in-proc by T8 against a stub; the literal run was
  skipped to avoid killing the machine's live Ollama).
- Daily-drive Vessel under a real agent tool for one task.
