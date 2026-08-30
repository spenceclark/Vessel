# Phase 2 — Implementation Report

> Status: **complete** (automated gate). The daily-drive soak and live OpenAI/Anthropic
> recordings remain human-at-the-keyboard items.
> Spec: [phase-2.md](phase-2.md) · Plan: [plan.md](plan.md) · Design authority: [architecture.md](architecture.md)

## What was built

Format detection + three adapters running in the background writer, off the request path,
per the spec's §2 layout:

| Piece | File | Notes |
|---|---|---|
| Enricher entry + backstop | `src/Vessel/Formats/FormatEnricher.cs` | D1; catches any detection/adapter throw → `raw` + `parse_error`, bytes intact |
| Enriched record | `src/Vessel/Formats/EnrichedRecord.cs` | Wraps the `CaptureRecord` + normalized fields |
| Detection | `src/Vessel/Formats/FormatDetector.cs` | D2: path suffix → payload sniff → backend-type tiebreak → `raw` (silent) |
| SSE parser | `src/Vessel/Formats/SseParser.cs` | D4: LF/CRLF, multi-`data:`, comments, `[DONE]`, `event:`, truncation-tolerant |
| NDJSON splitter | `src/Vessel/Formats/NdjsonParser.cs` | D4: skips an unparseable final fragment |
| OpenAI adapter | `src/Vessel/Formats/OpenAiChatAdapter.cs` | D5/D6: SSE delta fold → synthesized `chat.completion`; usage/cached tokens |
| Anthropic adapter | `src/Vessel/Formats/AnthropicMessagesAdapter.cs` | D5/D6: event-type fold → synthesized `message`; `tokens_in` sums input + both cache counts |
| Ollama adapter | `src/Vessel/Formats/OllamaAdapter.cs` | chat + generate; NDJSON fold; exact `eval_count/eval_duration` tok/s; `cold_load` |
| Text flattening | `src/Vessel/Formats/TextFlattener.cs` | D9: FTS/preview text; tool calls named, images/tool-defs skipped, thinking included |
| Estimation | `src/Vessel/Formats/TokenEstimator.cs` | D8: ceil(len/4), fills missing counts only |
| Warnings vocab | `src/Vessel/Formats/Warnings.cs` | D7 codes + canonical ordering |
| Body decoding | `src/Vessel/Formats/BodyDecoder.cs` | D3: gzip/deflate/br/zstd into a scratch buffer for parsing; wire bytes untouched |
| Store | `src/Vessel/Storage/SqliteCaptureStore.cs` | Enrichment columns + FTS insert; retention deletes FTS rows in the same transaction (D10) |
| Writer | `src/Vessel/Capture/CaptureWriterService.cs` | Enrich per record; per-batch resilience (drop + continue; give up after 5) |
| Inject usage | `src/Vessel/Proxy/ProxyHandler.cs` + `ConcatStream.cs` | D11: the one opt-in request-path mutation; stores original bytes + `usage_injected` |
| Config | `src/Vessel/Config/*` | `warnings.slowTtftMs` (default 5000); per-backend `injectStreamUsage` + startup warning |

Carry-ins from the Phase 1 review, both done: writer resilience (a failing batch is
dropped and logged, the loop continues, give-up only after 5 consecutive failures), and
the `LastResponseByteMs` mark stamped on every response-tee write (tok/s denominator).

No schema migration — schema v1 already had every column this phase populates.

## Verification results

### Automated tests — 135/135 pass (`dotnet test`; 71 prior + 64 new)

| # | Coverage | Where |
|---|---|---|
| F1 | Golden suite — 25 fixture cases, exact match on every field incl. synthesized `response_body` | `AdapterGoldenTests` |
| F2 | SSE parser units (LF/CRLF, multi-line, comments, `[DONE]`, mid-byte cut) | `SseParserTests` (9) |
| F3 | Detection: path suffix, prefix-less sniff, backend tiebreak, embeddings stays raw | `FormatDetectorTests` (14) |
| F4 | Error rows enrich from the request side (dead backend → `ollama-chat`, model + prompt, response null) | `EnricherIntegrationTests` |
| F5 | Estimation fills missing only, reported never overwritten, mixed row flagged | golden `streamed-no-usage`, `FormatEnricherTests` |
| F6 | tok/s: Ollama exact, streamed non-Ollama wire-timing, non-streamed non-Ollama NULL | golden fixtures |
| F7 | One code per warning (incl. `cold_load` suppressing `slow_ttft`) | golden `streamed-coldload`/`slow-ttft`/etc. |
| F8 | FTS: parsed rows searchable, raw absent, both retention caps leave zero orphans | `FtsRetentionTests` (3) |
| F9 | injectStreamUsage on/off + every skip condition (has options, non-JSON, not streamed, Content-Encoding, over-cap) | `InjectStreamUsageTests` (7) |
| F10 | Writer resilience: one throw dropped, later batches land; 5 in a row → give up | `CaptureWriterResilienceTests` (2) |
| F11 | Enricher backstop: forced adapter throw → `raw` + `parse_error`, bytes intact | `FormatEnricherTests` |
| F12 | Phase 0 T7 / Phase 1 C2 still green — enrichment is writer-side, invisible to chunk timing | prior suite still passes |

Fixture tree: 25 cases across `ollama-chat`, `ollama-generate`, `openai-chat`,
`anthropic-messages`, and `raw` — streamed + non-streamed happy paths, tool calls,
`length`/`max_tokens` truncation, mid-stream cut, cold-load, cache tokens, thinking,
gzip-encoded, http-error, bad-encoding, and garbage.

### Publish smoke (win-x64, self-contained, single file)

| Configuration | Size | Result |
|---|---|---|
| Untrimmed (shipping) | 101.9 MB | all checks pass |
| `PublishTrimmed=true` | — | zero trim warnings (kept clean; see finding 1) |

No new packages — `System.IO.Compression` is in-box; `ZstdSharp` was already present.

## Deviations and findings

Recorded in place in [phase-2.md](phase-2.md) §1 ("Deviations found during
implementation"); summary:

1. **Trim-clean kept.** `JsonArray.Add(...)` bound to the reflection-based generic
   `Add<T>` when the argument was typed `JsonObject`, adding two IL2026 trim warnings.
   Returning `JsonNode` from the OpenAI adapter's choice/tool-call builders rebinds it to
   `IList.Add(JsonNode?)` — trimmed publish is warning-free again.
2. **`http_error` / `proxy_error` made mutually exclusive** (D7 refinement) so each code
   means one thing and F7's "one fixture → one code" holds.
3. **Estimation gated to status < 400** (D8 refinement) so backend HTTP errors stay
   exact-or-null.
4. **Request-body drain on error paths** so a dead backend still yields a browsable row
   with model + prompt (F4).
5. **Fixtures hand-authored** to the documented wire shape (no live backend at authoring
   time); `verify/record-fixtures.ps1` re-records them wire-true, and `.gitattributes`
   marks the fixture tree binary so byte-exact bytes survive checkout.

## Acceptance criteria status

| # | Criterion | Status |
|---|---|---|
| 1 | §3 tests green, full prior suite green | ✅ 135/135 |
| 2 | `verify.ps1` asserts enriched row fields per Ollama case (incl. SSE synthesized body) | ✅ implemented (needs live Ollama to run) |
| 3 | Fixture tree committed for all three formats | ✅ hand-authored wire-true; live OpenAI/Anthropic recordings pending keys |
| 4 | Hand-browse shows enriched rows + `requests_fts MATCH` finds a phrase | ✅ covered by `FtsRetentionTests`/`EnricherIntegrationTests`; human check pending |
| 5 | Publish smoke still passes | ✅ untrimmed passes; trimmed warning-free |
| 6 | plan.md boxes ticked, deviations recorded | ✅ |

A day of real daily-drive traffic and the opt-in live-API fixture recordings are the
remaining human-at-the-keyboard items, as in phases 0 and 1.
