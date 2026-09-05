# Phase 5b — MCP Server (read-only v1): Implementation Spec

> Expands Phase 5b of [plan.md](../plan.md). Design authority: [architecture.md](../architecture.md).
>
> **Goal:** the user's own AI tools (Claude Code, Cursor, anything speaking MCP)
> interrogate captured traffic — "why did my planner agent stall this afternoon?"
> answered by the agent querying Vessel directly. Read-only; the design work is
> token-budget shaping, not protocol plumbing.

## 0. Scope

**In:** Streamable HTTP MCP endpoint at `/vessel/mcp` on the existing Kestrel host,
via the official **ModelContextProtocol C# SDK**; four read-only tools
(`search_requests`, `get_request`, `get_stats`, `list_sessions`); token-budget
shaping rules; config kill-switch; docs/trust-boundary statements.

*(Amended by #49: search rows and request detail carry the `score` field, so an agent can
read which variant a human preferred. The surface stays read-only — there is deliberately no
`set_score` tool; scoring is HTTP-only.)*

**Out (explicitly):** any mutating tool (replay, clear, sessions, config — each would
need its own approval; agents triggering side effects is a separate decision); stdio
transport (a bridge only if a real client demands it — none of the primary targets
do); MCP resources/prompts (tools only in v1); auth (localhost is the boundary,
same as the UI — see D5).

**No schema migration.**

---

## 1. Key implementation decisions

### D1 — Transport and mounting

Streamable HTTP via the official SDK's ASP.NET Core integration, mapped at
`/vessel/mcp` — inside the reserved `/vessel` namespace, so it is never proxied,
never captured, and sits behind the D03 control-plane guards (loopback Host
validation; the mutation-origin rule is moot for read-only tools but the Host guard
still applies). Server identity: name `vessel`, version from the assembly.

### D2 — The four tools (contract)

All outputs are compact JSON in text content. Every tool description is written for
LLM consumption: it states what the tool answers, its defaults, and how to get more
(the description *is* UX here). Timestamps ISO-8601; token counts flagged when
estimated, mirroring the REST semantics.

- **`search_requests`** `(query?, backend?, model?, tag?, status? (ok|error),
  format?, sessionId?, warnedOnly?, limit=20 (max 100), before?)` — the same
  filter/FTS semantics as `GET /requests` (D1 of phase-4), same sanitized-token FTS
  rules. Returns compact rows: `id, startedAt, method, path, backend, model, tags,
  statusCode, error, durationMs, ttftMs, tokPerSec, tokensIn, tokensOut, stopReason,
  warnings`, plus `promptPreview` (first ~200 chars of flattened prompt text), plus
  `nextBefore` for paging. **No bodies.**
- **`get_request`** `(id, include = "text" | "raw", maxChars = 4000, offset = 0)` —
  summary fields plus, for `include:"text"`, the **flattened prompt and response
  text** windowed by `offset/maxChars`, each with `totalChars` and an explicit
  `truncated: true` + "call again with offset=N" note *inside the payload* (the
  model reading it must be told, not left to infer). `include:"raw"` windows the
  decoded raw bodies under the same budget. Binary content is never inlined —
  reported as `{ binary: true, bytes: N }`.
- **`get_stats`** `(sessionId? = "current" | "all" | id)` — the REST stats payload
  (totals, failures, averages, token totals + estimated flag).
- **`list_sessions`** `(limit = 20)` — id, startedAt, name, newest first.

### D3 — Token-budget shaping (the actual design)

Defaults are conservative and every truncation is *self-describing*: 20 compact rows
per search; 4 000 chars per body window; previews ~200 chars; nothing base64. The
failure mode this prevents is the tool dumping a 200K-token agent context into the
caller's window on the first call — the agent must always *choose* to page deeper.
Hard caps: `limit ≤ 100`, `maxChars ≤ 20 000` per call.

**Read-time flattening:** `prompt_text`/`response_text` are not stored columns —
they live only in the contentless FTS index, which cannot be read back. `get_request`
therefore re-runs the existing flatteners (`TextFlattener` et al.) over the decoded
stored bodies at read time, off the writer thread, same code path as enrichment used.
Cheap at interactive rates; never on the proxy path.

### D4 — Implementation shape

A thin projection over `SqliteReadStore` — the queries all exist; the MCP layer maps
tool params → existing read methods → compact DTOs. One new read helper for the
flatten-at-read path. No writer involvement anywhere. STJ source-generated DTOs in
the established single-context-file pattern. SDK trim-compatibility is checked by
the publish smoke in this phase, not discovered at Phase 6 (the standing risk rule).

### D5 — Trust boundary and config

Same boundary as the UI: localhost bind + D03 Host guard, no additional auth. That
is a *statement*, not an accident, and it has a consequence the docs must say
plainly: **any MCP client you connect can read your captured prompts.** Config:

```jsonc
"mcp": { "enabled": true }   // default ON; kill-switch honored live (ConfigStore)
```

Disabled → `/vessel/mcp` returns 404 `not_found` (the D5 marking convention).
`/vessel/api/status` reports `mcp: { enabled }` so the UI/config panel can show it.
The Phase 6 README and the bind-address banner both mention MCP exposure when
binding beyond loopback.

### D6 — Client setup documented, not assumed

The spec's deliverable includes the two-liner users actually need, verified against
a real client during the manual gate:

```
claude mcp add --transport http vessel http://127.0.0.1:4550/vessel/mcp
```

---

## 2. New/changed layout

```
src/Vessel/
  Mcp/McpEndpoint.cs             # D1 mounting + enabled gate
  Mcp/McpTools.cs                # D2 four tools
  Mcp/McpDtos.cs                 # compact shapes + json context
  Storage/(SqliteReadStore)      # flatten-at-read helper (D3)
  Config/(VesselConfig)          # mcp.enabled
tests/Vessel.Tests/McpTests.cs   # in-proc SDK client against the real host
```

## 3. Automated tests

| # | Assertion |
|---|---|
| M1 | In-proc MCP client lists exactly the four tools; descriptions non-empty; schemas validate |
| M2 | `search_requests` parity: each filter + FTS query returns the same ids as the REST list for a seeded mix; hostile FTS input never errors; `limit` capped at 100; paging via `before` has no gap/overlap |
| M3 | `get_request` windows: `totalChars` correct; `truncated` note present exactly when windowed; `offset` paging reassembles the full text; binary body → `{binary, bytes}`, never inlined; unknown id → tool error, not protocol fault |
| M4 | `get_stats`/`list_sessions` parity with REST; estimated-token flag surfaces |
| M5 | `mcp.enabled: false` → 404 with `X-Vessel-Error`; live config PUT toggles it without restart; proxied paths unaffected throughout |
| M6 | D03 Host guard applies to `/vessel/mcp` (hostile Host rejected) |
| M7 | Publish smoke: SDK is single-file/trim clean; endpoint serves from the published exe |

## 4. Manual gate

1. `claude mcp add` against a running Vessel; from Claude Code, ask a real question
   ("find my truncated requests from today and tell me why they truncated") and
   watch it chain `search_requests` → `get_request` usefully.
2. Confirm a large captured context (100K+ chars) never arrives in one tool result.
3. Toggle `mcp.enabled` in the config panel; confirm live.
4. plan.md Phase 5b ticked; README/status items recorded for Phase 6 pickup.

## 5. Acceptance

Suites green; publish smoke incl. MCP; manual gate item 1 demonstrated end-to-end
with a real MCP client on real captured traffic.

## 6. Implementation record

- Implemented the official `ModelContextProtocol.AspNetCore` SDK as a stateless
  Streamable HTTP server at `/vessel/mcp`, with exactly the four D2 read-only tools.
  The host guard applies before the endpoint, and `mcp.enabled` is a default-on,
  live `ConfigStore` setting. Disabled MCP returns the normal Vessel `404 not_found`
  response with `X-Vessel-Error`; `/vessel/api/status` reports `mcp.enabled`.
- `get_request` recreates flattened text from decoded stored request/reassembled response
  bodies in `SqliteReadStore`. It does not read from contentless FTS and does not involve
  the writer or proxy paths. Search previews use this same read-side path; binary bodies
  are represented only by `{ binary: true, bytes: N }`.
- Automated M1–M6 coverage is in `McpTests.cs`, using the official in-proc SDK HTTP
  client against the real Kestrel host. The publish smoke now sends a Streamable HTTP
  `initialize` request to the published executable (M7).
- **Trim-compatibility finding (Phase 6 blocker):** ordinary self-contained single-file
  publish passes the MCP smoke. A `win-x64` publish with `PublishTrimmed=true` starts and
  serves `/vessel/api/status`, but `/vessel/mcp` returns 404. The SDK endpoint/tool
  registration is therefore not trim-safe in the current configuration. This was checked
  now, as required; it must be resolved before Phase 6 enables trimming. The untrimmed
  shipping publish smoke remains green.
- Manual gate item 1 remains deliberately unticked: a human must run
  `claude mcp add --transport http vessel http://127.0.0.1:4550/vessel/mcp` against live
  captured traffic and confirm the useful `search_requests` → `get_request` flow.

## 7. Post-sign-off follow-up (COMPLETED)

- ✅ **OAuth discovery probes no longer pollute capture.** MCP clients connecting to
  `/vessel/mcp` probed the origin root for auth metadata per the MCP auth spec:
  `/.well-known/oauth-authorization-server*`, `/.well-known/oauth-protected-resource*`,
  `/.well-known/openid-configuration*`. These now are reserved as **control plane**
  (same rationale as `/vessel/*` — the requests are addressed to Vessel's MCP surface,
  not the backend): answered directly with `404 not_found` + `X-Vessel-Error` marking
  convention, never proxied, never captured. A backend that genuinely serves these
  paths remains reachable via `/b/{backend}/...` (known edge, documented in
  architecture.md §3.2). NOT method-based filtering — capture-everything stands
  (only these exact path prefixes are reserved).
  - **Favicon.ico served as control-plane.** `/favicon.ico` is now served
    (the embedded Vessel SVG mark) as control-plane, with cache headers
    (`Cache-Control: public, max-age=31536000`). Never proxied, never captured.
  - **Implementation:** `WellKnownEndpoints.cs` middleware in `VesselApp.Build`
    (before the proxy catch-all) intercepts and answers both paths.
  - **Tests:** in-proc MCP client connect cycle leaves zero captured rows
    and zero failed stats; well-known probes return `404 not_found +
    X-Vessel-Error`; paths under `/b/{backend}/.well-known/...` still
    proxy normally; favicon serves with correct content-type and cache headers.
  - **Documentation:** routing section of architecture.md (§3.2) updated
    with reserved prefixes list.
