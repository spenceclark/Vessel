# Phase 5b — Review Report (uncommitted changes)

Reviewer pass over the uncommitted working tree against [phase-5b.md](phase-5b.md),
[plan.md](plan.md), and the project rules in [AGENTS.md](../AGENTS.md). Build is clean
(0 warnings). **The test suite is not green: 311 tests, 2 failing** — both in the new
`McpTests.cs`. This directly contradicts the "Suites green" acceptance claim in
phase-5b.md §5 and the "Automated M1–M6 coverage … green" / follow-up "Tests" claims in
§6–§7.

## Summary of verdict

The core MCP surface (four read-only tools, config kill-switch, host guard, token-budget
shaping, well-known reservation) is implemented cleanly and matches the D1–D6 design. The
in-proc SDK tests M1–M6 pass. Two problems block acceptance, one of them a real product
bug, plus one performance issue and minor doc drift.

---

## Findings

### 1. HIGH — Favicon endpoint serves a `data:` URI string as the response body (product bug)

`src/Vessel/Api/WellKnownEndpoints.cs:30-40` (`HandleFavicon`) writes a **`data:image/svg+xml,%3Csvg…` URI string** as the HTTP response body while setting `Content-Type: image/svg+xml`. A browser requesting `/favicon.ico` receives the literal text `data:image/svg+xml,%3Csvg…` where the markup is percent-encoded (`%3Csvg`, not `<svg`). That is not a valid SVG document — the favicon will not render.

- Failing test that proves it: `Favicon_IsServedAsControlPlane` — `Assert.Contains("<svg", content)` fails because the served body contains `%3Csvg`, not `<svg`.
- Fix direction (not applied — review only): write the raw/decoded SVG markup as the body, not a `data:` URI wrapper. The `data:` form belongs in an `href`/`src` attribute, not in a resource response body.

### 2. HIGH — Two failing tests are shipped in the changeset; acceptance claims are false

Rule (AGENTS.md non-negotiable): *never leave failing tests; suites must be green before a phase is claimed done.* phase-5b.md §5/§6/§7 assert the suites are green. They are not.

- `Favicon_IsServedAsControlPlane` — fails on the real bug in finding #1.
- `WellKnownPaths_UnderBBackendPrefix_AreProxied` (`McpTests.cs:368-387`) — fails at `Assert.Single(rowArray)` with "collection was empty". **This is a test defect, not a product bug:** the test issues the proxied request then immediately queries `/vessel/api/requests`, but capture is written asynchronously by the background writer. Every other capture-asserting test waits via `CaptureDb.WaitUntil(...)` (see `CaptureIntegrationTests.cs:164`); this new test does not, so it races the writer and usually sees zero rows. The three sibling "never captured" tests (`WellKnownPaths_AreControlPlane…`, `McpClientConnectCycle…`, favicon's empty-DB check) assert *emptiness*, which the same race satisfies trivially — so they pass today but give weak assurance (a path that *was* captured could still slip through if the writer hasn't flushed).

Both must be resolved (fix the favicon code for #1; add a `WaitUntil` / capture-flush wait for the proxied-path assertion) before the phase can be accepted. Per the rules I have **not** modified or weakened either test.

### 3. MEDIUM — `search_requests` does full per-row flatten work for a 200-char preview (N+1)

`McpTools.SearchRequests` (`McpTools.cs:39-47`) calls `store.GetMcpRequest(summary.Id)` **for every row** solely to compute `promptPreview`. `GetMcpRequest` decompresses and decodes both stored bodies **and flattens both prompt and response text** (`SqliteReadStore.GetMcpRequest` → `FlattenPrompt` + `FlattenResponse`). For a `limit=100` search that is 100 body decompress/decode cycles plus 200 flatten operations, of which the 100 **response** flattens are pure waste (the preview only uses `PromptText`). D3 sanctions read-side flattening "at interactive rates," but flattening the response for every search row is avoidable. Consider a lighter read helper that returns only the prompt text (or a bounded prefix) for the preview path.

### 4. LOW — `plan.md` dangling link to the deleted spec

`docs/phase-5-mcp.md` was deleted and replaced by `docs/phase-5b.md`, but `plan.md:171` still reads `Spec: [phase-5-mcp.md](phase-5-mcp.md).` — a broken link. The surrounding lines in the same block were edited in this changeset, so this is a missed update, not pre-existing.

### 5. LOW / NOTE — Coverage gap and minor scope creep

- **Flatten-at-read is only tested for `openai-chat`.** M3 seeds and asserts the `openai-chat` path (`"user: " + prompt`). The `FlattenPrompt`/`FlattenResponse` switches also cover ollama-chat/generate, openai-responses, and anthropic-messages, none of which are exercised. The preview/`get_request` text for those formats is unverified. Not a blocker, but worth a fixture each given this is the phase's core value.
- **Phase 6 material added in a Phase 5b change.** `plan.md` gains a `Spec: [phase-6.md]` reference and a new Phase 8 "Tray app" bullet, and `docs/phase-6.md` is added. AGENTS.md asks to *implement what was asked* and keep adjacent changes out of the same change. Harmless as docs, but flagged for scope hygiene.

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
- Full xUnit v3 run → `Total: 311, Failed: 2` (the two above); all other suites green.
- Confirmed finding #2's second failure is a writer-race by comparing against the
  `CaptureDb.WaitUntil` pattern used by every other capture-asserting test.
