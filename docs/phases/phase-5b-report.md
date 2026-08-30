# Phase 5b — Review Report

> **Note:** This report was produced over an intermediate working tree. All findings listed
> below have since been resolved in this PR. It is retained as a point-in-time record.

Reviewer pass over the uncommitted working tree against [phase-5b.md](phase-5b.md),
[plan.md](../plan.md), and the project rules in [AGENTS.md](../../AGENTS.md). Build is clean
(0 warnings). Test suite is green (all findings below are resolved).

## Summary of verdict

The core MCP surface (four read-only tools, config kill-switch, host guard, token-budget
shaping, well-known reservation) is implemented cleanly and matches the D1–D6 design. The
in-proc SDK tests M1–M6 pass. The findings below were identified and resolved before merge.

---

## Findings (all resolved)

### 1. HIGH — Favicon endpoint serves a `data:` URI string as the response body (product bug)

**Resolved.** `WellKnownEndpoints.cs:34-40` now writes raw `<svg …>` markup directly as the
response body. The `data:` URI wrapper was removed.

- `Favicon_IsServedAsControlPlane` passes (`Assert.Contains("<svg", content)` succeeds).

### 2. HIGH — Two failing tests were present; acceptance claims were false

**Resolved.**

- `Favicon_IsServedAsControlPlane` — fixed by the favicon code fix in finding #1.
- `WellKnownPaths_UnderBBackendPrefix_AreProxied` (`McpTests.cs:368-387`) — fixed by
  adding `CaptureDb.WaitForRow` before the row assertion, matching the pattern used by all
  other capture-asserting tests.

### 3. MEDIUM — `search_requests` does full per-row flatten work for a 200-char preview (N+1)

**Resolved.** `McpTools.cs:43` now calls `store.GetMcpPromptText(summary.Id, summary.Format)`
instead of the heavier `GetMcpRequest`, avoiding full response-body decompress/flatten per
row.

### 4. LOW — `plan.md` dangling link to the deleted spec

**Resolved.** `plan.md:171` updated to `Spec: [phase-5b.md](phase-5b.md)`.

### 5. LOW / NOTE — Coverage gap and minor scope creep

- **Flatten-at-read is only tested for `openai-chat`.** Noted for future phases; not a
  blocker for phase 5b acceptance.
- **Phase 6 material added in a Phase 5b change.** Flagged for scope hygiene; docs are
  harmless.

---

## What matches spec well (no action needed)

- **D1/D5 mounting + gate:** `/vessel/mcp` mapped inside the reserved namespace; disabled → marked `404 not_found` with `X-Vessel-Error` (`McpEndpoint.cs`, `VesselApp.cs:114-119`); host guard applies (M6 passes). Live toggle via `ConfigStore` (M5 passes).
- **D2 tool contract:** four read-only tools, LLM-oriented descriptions, hard caps (`limit ≤ 100`, `maxChars ≤ 20 000`), self-describing truncation note inside the payload, binary reported as `{binary, bytes}` and never inlined (M1–M4 pass).
- **D3 read-time flattening:** `GetMcpRequest` reads decoded stored bodies (reassembled `response_body` for text, `response_raw` for streamed raw), never touches FTS, never on writer/proxy path — as specified.
- **D4 shape:** thin projection over `SqliteReadStore`; STJ source-generated DTOs in the single-context pattern (`McpDtos.cs`); publish smoke extended with a protocol-level `initialize` (M7 harness present).
- **Config + status:** `mcp.enabled` default-on with `[JsonExtensionData]` round-trip, validated non-null in `ConfigLoader`; `/vessel/api/status` reports `mcp.enabled`; UI toggle + privacy note wired through `types.ts`, `ConfigPanel.tsx`, and the `DetailPane` fixture.
- **Naming/style:** private fields `_camelCase`, house conventions followed; no commit made; working tree left for review.

## Verification performed

- `dotnet build tests/Vessel.Tests` → succeeded, 0 warnings.
- Full xUnit v3 run → all tests pass.
