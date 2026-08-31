# Phase 4 — Search, Filters, Rendering, Config: Implementation Spec

> Expands Phase 4 of [plan.md](../plan.md). Design authority is [architecture.md](../architecture.md);
> this spec makes the concrete decisions that document deliberately left open.
>
> **Goal:** findability and readability at 10k requests. "Find that request from this
> morning where the agent got truncated" takes under ten seconds: filter warnings-only
> or search a phrase, click, read. Plus the config editor — the one item in this phase
> with real backend design work (live-apply).

## 0. Scope

**In:** FTS search wired into the list (§6.2); full filter bar (backend, model, tag,
status, format, session, warnings-only) (§7); facets for the dropdowns; rendered
message view with markdown + collapse; tool calls as cards (§5.2); rate-limit +
cache-token display on Overview (§10); clear-all / clear-before (§6.4); config
editor with **live apply** (§7, §9); two carry-in fixes from the Phase 3 review.

**Out (explicitly):** replay / diff / copy-as-curl, cost estimates, context-growth
chart, Ollama panel (all Phase 5); auth for the UI (out of scope entirely — localhost
binding is the boundary, §8); URL/router state for filters (no router, by design).

**No schema migration.** FTS and every queried column already exist.

---

## 1. Carry-ins from the Phase 3 review (fix first, small)

- **C1 — detail id parse:** `RequestsEndpoints.Detail` does `long.Parse` on the route
  value — `/vessel/api/requests/abc` throws `FormatException` → unhandled 500.
  `TryParse` → existing 404 `not_found` path. Test: non-numeric and overflowing ids.
- **C2 — SSE reconnect leaves the list stale:** after a dropped `EventSource`
  connection (laptop sleep, Vessel restart), events that fired during the gap are gone
  — the list stays stale until a manual reload. In `useEvents`, on `open` following a
  disconnect (not the initial open), invalidate the `requests` and `stats` queries so
  the REST refetch closes the gap. Manual test is fine (kill/restart Vessel with the
  tab open); no automated harness exists for the frontend.

---

## 2. Key implementation decisions

### D1 — List filtering: extend `GET /requests`, keep the cursor

New query params, all combinable with `limit`/`before`/`session`:

| Param | Semantics |
|---|---|
| `q` | FTS5 `MATCH` over `requests_fts`. The user string is sanitized: split on whitespace, each token wrapped in `"…"` (escaping embedded quotes by doubling), joined with implicit AND — users get phrase-ish search and can never hit FTS syntax errors (`AND`, `(`, `*` are literals). Empty after sanitize → param ignored. |
| `backend` | exact, case-insensitive |
| `model` | exact |
| `format` | exact (`raw` included) |
| `tag` | exact element match via `EXISTS (SELECT 1 FROM json_each(requests.tags) WHERE json_each.value = $tag)` — the bundled e_sqlite3 has JSON1; never `LIKE` over the JSON text |
| `status` | `ok` (status < 400 and no error) \| `error` (status ≥ 400 or error set) |
| `warned` | `1` → `warnings IS NOT NULL` |

`q` joins `requests_fts` on `rowid = requests.id` with the same `id < $before` cursor —
pagination and filters compose. All non-`q` filtering stays on `requests` columns.
The response shape is unchanged (Phase 3 promised that).

### D2 — Facets: `GET /requests/facets?session=`

Returns `{ backends: string[], models: string[], tags: string[], formats: string[] }` —
distinct values scoped like the list (session or all), each list capped at 100,
alphabetical. Tags via `json_each` over the scoped rows. No counts (the dropdowns
don't need them; keep the query cheap). Fetched once per scope change, not per
keystroke.

### D3 — Filter bar UI

One row above the list: debounced (300 ms) search input, dropdowns for backend /
model / format (from facets, hidden when only one value), tag picker (facet chips),
status toggle (all | ok | error), warnings-only toggle. Active filters render as
removable chips; a "clear filters" affordance appears when any is active. Filter state
is React state only (no router — D6 of phase 3 stands). The `q` input hits the list
query directly; everything is `useInfiniteQuery` on a key that includes the filter
object, so paging Just Works per filter combination. Live `completed` inserts (phase 3)
are suppressed while any filter other than session scope is active — a new row may not
match the filter, and refetching on every completion defeats the cache; instead show a
subtle "new requests — refresh" pill when completions arrive while filtered.

### D4 — Rendered message view (client-side, per-format)

Rendering is entirely client-side from the detail payload — the API already returns
bodies and `format`. `frontend/src/render/` gets per-format extractors to one
normalized view model:

```
{ system?: string, messages: { role, blocks: Block[] }[], params: {k,v}[] }
Block = text | markdown-rendered; image (placeholder chip, click to view — data is
        already local); toolUse {id?, name, argsJson}; toolResult {forId?, content};
        thinking (collapsed by default)
```

- `openai-chat`: request `messages[]` (incl. `tool` role → toolResult), `tools` defs
  summarized as a collapsible params entry; response from the (reassembled) body's
  `choices[0].message`.
- `anthropic-messages`: `system` + `messages[]` content blocks (`tool_use`,
  `tool_result` matched by id), response message blocks incl. `thinking`.
- `ollama-chat` / `ollama-generate`: messages / prompt; response message or `response`
  string; the final-object metrics render as params.
- `raw` and any extraction failure: fall back to the existing PrettyJson view — the
  Phase 3 raw toggle remains available on every tab regardless of format.

Markdown via `react-markdown` + `remark-gfm` (the one new runtime dependency this
phase; no syntax-highlighter — revisit only if genuinely missed). Text blocks over
~4 000 chars render clamped with an expand control. Tool-call cards: name + args
pretty-printed, collapsible, result card visually linked when the id matches.

### D5 — Overview additions

- **Rate limits:** client-side scan of `responseHeaders` for `x-ratelimit-*` /
  `anthropic-ratelimit-*`; grouped table (limit / remaining / reset), shown only when
  present. No backend work.
- **Cache tokens:** `tokensCachedRead` / `tokensCachedWrite` already in Summary —
  render alongside tokens in/out (with the Anthropic "in includes cached" note from
  phase-2 D6 as a tooltip).

### D6 — Clear-down runs on the writer (single-writer invariant)

`DELETE /vessel/api/requests?scope=all` or `?before=<ISO-8601>` → enqueued as a writer
command (the phase-3 `CaptureWork` union gains `ClearCommand`), which deletes matching
`requests` rows + their FTS rows in one transaction, then `incremental_vacuum`.
Response `{ deleted: n }` via the command's `TaskCompletionSource`. UI: a "Data"
section behind a gear icon next to the stats bar — Clear all / Clear before date, both
with typed-confirmation dialogs. Retention config lives in the same section (D7).
At Phase 4, session markers were not deleted. Issue #41 later added atomic rows+marker
session deletion: one named target uses a count-showing picker confirmation, while the
Data panel uses typed confirmation for multi-select bulk deletion; current remains protected.

### D7 — Config editor + live apply (the real design work this phase)

**Answering the open design question:** architecture.md §7/§9 promised `GET/PUT
/vessel/api/config` and "editable in the UI" but never designed apply semantics —
Phase 0 loads config once and singletons cache values (`BackendRegistry`,
`ProxyHandler`'s timeouts/caps, the writer's retention, `FormatEnricher`'s
backend-type map). This decision closes that gap; fold the essentials back into
architecture.md §9 when the phase lands.

- **`ConfigStore`** (new singleton) owns the current `VesselConfig` as an immutable
  snapshot with a version counter. `Current` is a `Volatile.Read` of the snapshot ref.
- **Consumers stop caching:** `BackendRegistry` becomes a view over
  `ConfigStore.Current` rebuilt on version change (cheap: rebuilt lazily when the
  version differs); `ProxyHandler` reads timeouts/`maxBodyMb` per request from the
  snapshot (a field read, not re-parsing); the writer re-reads retention each batch;
  `FormatEnricher` re-derives its backend-type map on version change. The shared
  `HttpMessageInvoker` is config-independent and stays put.
- **`GET /vessel/api/config`** → the full current config (nothing in it is secret —
  Vessel holds no keys). **`PUT`** → validate with the *same* `ConfigLoader` rules as
  startup (bad config → 400 with the validation message, nothing applied), persist to
  `vessel.json` (`JsonExtensionData` keeps unknown props), swap the snapshot, bump the
  version. Serialized under a lock; last write wins (single user, single machine).
- **Restart-required fields:** `listen` only. PUT still persists it but responds
  `{ applied: true, restartRequired: ["listen"] }`; the UI banners "listen address
  changes on next start". Everything else — backends (add/edit/remove), default
  backend, retention, capture caps, timeouts, warnings, injectStreamUsage — applies
  live. In-flight requests keep the snapshot they started with (they hold the old
  `ResolvedBackend`); only new requests see changes. Removing a backend never aborts
  in-flight traffic to it.
- **UI:** a Config panel (same gear area as D6): backends table (name, baseUrl, type,
  injectStreamUsage, default radio; add/remove), retention + capture + slow-TTFT
  numbers, listen (with the banner). Client-side required-field checks; the server
  validation message surfaces verbatim on 400. On successful save: invalidate
  `/status` + facets.
- This touches Phase 0–2 singletons — it's the piece of this phase most worth a
  careful review pass (and the reason this phase shouldn't be rushed even though most
  of it is UI).

### D8 — JSON contexts

Per the phase-3 finding: every new API `[JsonSerializable]` type (facets, config DTOs,
clear response) goes in the existing `Api/ApiJsonContext.cs` — one context per
concern is fine, one *file* per partial context is not.

---

## 3. New/changed layout

```
src/Vessel/
  Api/ConfigEndpoints.cs        # D7 GET/PUT
  Api/FacetsEndpoint.cs         # D2
  Config/ConfigStore.cs         # D7 snapshot holder
  (RequestsEndpoints, SqliteReadStore — D1 filters, facets query, C1)
  (CaptureWork, CaptureWriterService, SqliteCaptureStore — D6 ClearCommand)
frontend/src/
  render/{types,openai,anthropic,ollama}.ts   # D4 extractors
  components/{FilterBar,MessageView,ToolCallCard,ConfigPanel,DataPanel}.tsx
  (DetailPane — rendered view default, raw toggle kept; Overview — D5)
  (useEvents — C2; RequestList — D3 filter-aware inserts)
tests/Vessel.Tests/
  FilterTests.cs                # D1/D2: every filter, combinations, FTS sanitize
  ClearTests.cs                 # D6
  ConfigApplyTests.cs           # D7
```

## 4. Automated tests

| # | Assertion |
|---|---|
| V1 | C1: `/requests/abc` and `/requests/99999999999999999999` → 404 JSON, not 500 |
| V2 | `q`: matches prompt and response text; hostile input (`"foo" AND (bar* NEAR`) returns 200 and treats operators as literals; `q` composes with cursor paging (no gap/overlap) |
| V3 | Each filter alone + a three-way combination (`backend`+`status=error`+`warned=1`) returns exactly the seeded matches; `tag` uses element match (tag `a` never matches row tagged `abc`) |
| V4 | Facets: scoped to session, distinct, capped, alphabetical |
| V5 | Clear all / clear before: rows + FTS rows gone atomically, later rows intact, `{deleted}` accurate, vacuum ran (db size shrinks with incompressible seed data) |
| V6 | Config PUT: invalid (dup backend name, bad URL, non-positive retention) → 400 + nothing applied (GET returns old, file unchanged); valid → file rewritten with unknown props preserved |
| V7 | Live apply: PUT pointing backend `b` at stub B → next proxied request lands on B with zero dropped requests during the swap; retention change applies on the next batch; `listen` change → `restartRequired` and old listener still serving |
| V8 | In-flight isolation: a slow streamed request started before a PUT that removes its backend completes normally |
| V9 | Full prior suite green; T7/C2 timing tests unchanged |

## 5. Manual gate

1. 10k-row DB (seed script or a long soak): search + each filter feels instant;
   virtualized list stays smooth.
2. The plan's litmus test: find a truncated-response row from earlier today in under
   ten seconds, two different ways (warnings-only filter; text search).
3. Rendered view on real traffic: multi-turn agent conversation with tool calls reads
   cleanly; thinking collapsed; raw toggle still there; an image-bearing request shows
   the placeholder chip.
4. Config panel: add a second real backend, save, route to it via `/b/…` without
   restart; break the config in the editor and confirm the 400 message is human;
   clear-before leaves recent rows.
5. C2: restart Vessel with the tab open — list catches up on reconnect.

## 6. Acceptance criteria (phase gate)

1. §4 tests green; full suite green; frontend `tsc` clean.
2. Manual gate 1–5 done.
3. Publish smoke passes; architecture.md §9 updated with the live-apply model (D7).
4. plan.md Phase 4 boxes ticked; deviations recorded here.
