# Phase 7 — Charts & Reporting (issues #25 + #26): Implementation Spec

> Expands the charting bullets of [plan.md](../plan.md) Phase 7. Design authority:
> [architecture.md](../architecture.md) for the API and read store,
> [ui-spec.md](../ui-spec.md) for everything under `frontend/`.
>
> **Goal:** make two questions answerable without reading rows — *"is this agent's
> context bloating as the run goes on?"* (#25) and *"where did my tokens actually go?"*
> (#26). Both issues are specified here as one piece of work because #26 explicitly
> depends on the chart foundation #25 has to build first.

## 0. Scope

**In:** the ui-spec §2 chart-token groundwork (#25's blocking sub-step); two read-store
aggregations and the two endpoints over them; a small SVG chart component set built on
`d3-scale`/`d3-shape`; a Reports view reached from a header view toggle, carrying the
History filters; five report cards, of which the context-growth line chart is #25.

**Out (explicitly):** cost/spend reports (the `cost_estimate` column exists but §9
`pricing` config does not — that report ships with the pricing issue); per-model
tool-call fumble rates (gated on the fumble-detection warning); exporting a report;
a date-range picker (sessions are the run boundary — the same reasoning as #24);
comparing two sessions side by side; time-bucketed rate charts (requests/min);
streaming chart updates over SSE (see D14).

**No schema migration. No new indexes.** Both queries are covered by the existing
`ix_requests_session` plus full scans of a table capped at 10 000 rows by default
(`maxRequests`).

---

## 1. Decisions taken before this spec was written (S-table)

| # | Decision | Resolution |
|---|---|---|
| S1 | **Chart library.** ui-spec §8.5 makes any new UI dependency a doc-level decision. Measured with esbuild (minified, React 19 baseline subtracted; the app's current bundle is 542 KB min — `frontend/dist/assets/index-*.js`): Recharts 3.10 **+361 KB** (+67%), visx +77 KB, uPlot +52 KB, `d3-scale` + `d3-shape` **+43 KB** (+15 KB gzip). | **`d3-scale` + `d3-shape` behind our own SVG primitives.** Recharts drags `@reduxjs/toolkit`, `react-redux`, `immer` and `victory-vendor` into an app whose state model is deliberately TanStack Query plus one hook, and its inline-styled defaults must be overridden prop-by-prop to reach §2's tokens anyway — so its "free styling" is not free here. visx is the same compose-it-yourself model as option 1 (we write the same components) with a larger package graph and a React peer pin. uPlot is canvas: no DOM for §8.7 accessibility, and colors cannot come from CSS variables — it would have to read computed styles and re-create the chart on every theme flip. We need exactly two chart forms; take the smallest, most stable dependency. |
| S2 | **Where charts live.** The app is one screen with no router (architecture §10). | **A `History` / `Reports` segmented control in the header panel** (§6 Tabs). Reports replaces the list+detail row with one full-width, internally-scrolling panel. The header stays shared, so the session picker and stat strip carry over — which reports need. |
| S3 | **Report scope.** | **Reports carry the History filters**, matching #24 export's "what I'm looking at" rule. Because the FilterBar itself lives in the (hidden) list panel, the Reports view **must** render the active filters as visible, individually-clearable chips — a silently-filtered chart is a lie (D11). |
| S4 | **Chart CSP safety.** `/vessel/*` serves `script-src 'self'` with no `unsafe-eval` (`VesselApp.cs`, `ContentSecurityPolicyTests`). | Verified against bundled output: no candidate contains a global `eval` or `new Function`; `d3-scale`/`d3-shape` are pure math. Still an explicit gate item (§8) — asserted by loading the page, not by faith. |

Licenses for THIRD-PARTY-NOTICES.md: `d3-scale` ISC, `d3-shape` ISC, `@types/d3-*` MIT.

---

## 2. Blocking groundwork — ui-spec §2 chart tokens (#25's sub-step)

ui-spec §2.2 says charts "get their own tokens added here first". This lands **before any
chart code**, as its own commit, and is the whole of Batch B1.

### 2.1 New ui-spec section — §2.3 Chart tokens & forms

| Token | Value (both themes) | Use |
|---|---|---|
| `--chart-grid` | `color-mix(in srgb, var(--border-strong) 30%, transparent)` | gridlines — deliberately weaker than a panel border, so a chart never reads as a table |
| `--chart-axis` | `var(--text-muted)` | axis lines, ticks, tick labels |
| `--chart-1` … `--chart-6` | `--tag-blue`, `--tag-indigo`, `--tag-violet`, `--tag-pink`, `--tag-fuchsia`, `--tag-steel`, in that order | the categorical series ramp |

No new hex values enter the palette: the chart chrome is derived from existing tokens at
the §2.1-sanctioned percentages, and the series ramp *is* the existing tag ramp.

### 2.2 Rules that go in with them

- **§2.1's `--tag-*` group is re-titled the categorical ramp**, used by both tag pills and
  chart series, so a tag's line in a chart is the same color as its pill in a row. The
  ramp's existing exclusion of red/amber/green/cyan/teal now earns its keep twice: those
  hues stay free for status meaning *inside* charts too.
- **`--accent` is never a data color** — §2.2 already says this, and charts get no
  exception. `--danger` appears in a chart only for a genuinely failed quantity;
  `--ok`/`--warn` likewise stay semantic.
- **Series fills are derived, never new colors:** `color-mix(in srgb, <series> 20%,
  transparent)` — the same 10/14/20/30 rule as badge tints.
- **Chart form vocabulary:** time series → a **line** (multi-series) or **area + line**
  (single series only; overlapping fills are unreadable); categorical comparison →
  **horizontal bar**, optionally grouped (two measures) or two-part stacked (ok/failed);
  anything else → a table. **No pie or donut, no dual y-axes, no 3D, no gradients.**
- **Six series maximum** — the ramp size, and the readability ceiling. Past that the
  server ranks and the chart states what it omitted (D1).
- **Accessibility (§8.7):** every chart is a `<figure>` carrying an `aria-label` that
  summarizes it in one sentence, plus a visually-hidden `<table>` of the same data. The
  legend is always text, never color alone.
- **Motion (§7):** charts render statically. No entrance or transition animation on
  geometry; only the 150ms opacity transitions already allowed, for hover emphasis.
- **Numbers (§8.6):** every axis tick, tooltip value and table cell formats through
  `lib/format.ts`. Tick labels are `xs`, `--chart-axis`, `tabular-nums`.

### 2.3 Other ui-spec edits in the same commit

- §2.2 bullet: "charts (Phase 5) get their own tokens added here first" → "charts use
  §2.3's tokens and forms".
- §5: document the History/Reports toggle and the Reports panel in the layout section.
- §8.5 dependency list: add `d3-scale`/`d3-shape`, noting they supply **chart math only**
  (scales, ticks, path generation) and that all rendering is our own SVG under §2.3.

---

## 3. Backend

Two read endpoints. Both reuse `ConfigureFilteredCommand` — the one canonical list
predicate — so a report can never drift from the list and the export the way a second
query builder would.

### D1 — `GET /vessel/api/series`

The context-growth data (#25). One point per captured request, so the chart shows the real
shape of a run rather than a smoothed aggregate.

**Query:** `metric=tokens_in|tokens_out|tokens_total` (default `tokens_in`),
`groupBy=none|tag|model|backend` (default `none`), plus the full canonical list scope —
`session`, `q`, `backend`, `model`, `format`, `tag`, `status`, `warned`. `session` follows
`/requests` semantics exactly (absent or `all` = no scope; there is no `current` keyword
here — that alias is `/stats`-only). The capture-format filter keeps its plain name
`format`; the `requestFormat` alias exists only on `/export`, where `format` means the
file format.

**Response:**

```json
{
  "metric": "tokens_in",
  "groupBy": "tag",
  "series": [
    { "key": "planner", "points": [ { "id": 812, "t": "2026-08-31T09:12:04.113Z", "v": 18422 } ] },
    { "key": null,      "points": [] }
  ],
  "returned": 5000,
  "totalMatching": 12043,
  "truncated": true,
  "omittedSeries": 3,
  "estimated": true
}
```

- Points are **oldest-first by `id`** — insertion order is the true chronology;
  `started_at` can tie or skew. `t` is the ISO `started_at`; `id` is carried so a click can
  select that request (D13).
- `key` is `string | null`; null means the grouping column had no value (an untagged
  request, or a raw-fallback capture with no model). The UI renders it `(none)`.
- **Null-metric rows are excluded by predicate** (`<column> IS NOT NULL` joins the WHERE),
  so `totalMatching` and the returned points reconcile exactly — a raw capture with no
  token counts is not a silent gap in the count.
- **Cap `MaxPoints = 5000`.** When the predicate matches more, the **newest** 5000 are
  returned (`ORDER BY id DESC LIMIT 5001`, reversed in code) with `truncated: true`.
  `totalMatching` is computed (via the existing `CountRequests`) **only** when the cap was
  actually hit, so the common case pays for no second scan. The UI must say *"most recent
  5,000 of 12,043 requests"*; silent truncation is not acceptable.
- **Series cap = 6** (§2.3's ramp size). Series are ranked by total metric value and the
  remainder is **dropped, not merged** — folding leftover tags into an "(other)" line would
  sum unrelated requests at arbitrary timestamps and draw a curve that never happened.
  `omittedSeries` reports how many were dropped so the UI can disclose it.
- `estimated` is true when any contributing row had `tokens_estimated` set, so the whole
  chart can be flagged approximate (the same rule as `StatsResponse.TokensEstimated`).
- `groupBy=tag` fans out through `json_each`: a request with two tags contributes a point
  to **both** series. That is correct for "per-agent context growth", and is disclosed in
  the UI (D12).

### D2 — `GET /vessel/api/aggregate`

Every report in #26 from one endpoint — "keep the query set small and mechanical".

**Query:** `by=model|tag|backend|format` plus the same canonical list scope as D1.

**Response:**

```json
{
  "by": "model",
  "rows": [
    { "key": "qwen3:32b", "requests": 412, "failed": 3,
      "tokensIn": 8123456, "tokensOut": 214233,
      "tokensCachedRead": 0, "tokensCachedWrite": 0,
      "avgDurationMs": 3041.2, "avgTtftMs": 412.0, "avgTokPerSec": 47.3,
      "tokensEstimated": false }
  ],
  "totalGroups": 7
}
```

- Sorted by `tokensIn + tokensOut` DESC, then `requests` DESC, then `key` ASC — a total
  order, so the chart is stable across refetches.
- **Cap `MaxGroups = 50`**, with `totalGroups` reported. **No "(other)" rollup:** combining
  averages of averages is arithmetically wrong, and computing a correct remainder needs a
  second anti-join query for a row nobody asked for. A top-N bar chart is the normal form;
  the UI discloses *"top 50 of 137 by tokens"*. This matches the existing facets cap of 100,
  which also has no rollup.
- `failed` uses the stats predicate verbatim: `error IS NOT NULL OR status_code >= 400`.
- `avgTtftMs` averages **streamed rows only** (mirrors `GetStats`); every average ignores
  nulls; sums are `COALESCE(…, 0)`.
- `tokensEstimated` is `MAX(tokens_estimated)` per group.
- `by=tag` fans out through `json_each`, so a multi-tag request is counted **once per tag**
  and the rows can sum past the session total. Disclosed in the UI (D12) and stated in the
  endpoint's doc comment.

### D3 — Read-store implementation notes

- Add `GetSeries(SeriesQuery)` and `GetAggregate(AggregateQuery)` to `SqliteReadStore`,
  both built on `ConfigureFilteredCommand`.
- `ConfigureFilteredCommand` gains an optional tag fan-out: when the caller groups by tag it
  emits `FROM requests JOIN json_each(COALESCE(requests.tags, '[]')) AS tag_each` — the
  correlated table-valued-function pattern `GetFacets` already uses — composing with the FTS
  join and with an existing `tag=` filter's `EXISTS(…)` clause. **Confirm the join order
  against SQLite's TVF argument rules with a test that combines `q=`, `tag=` and
  `groupBy=tag` in one query**: that three-way combination is the one that breaks if the
  FROM clause is assembled in the wrong order.
- Grouping keys read as `string?`. `by=format` never yields null (`format` is NOT NULL);
  `model` and `tag` can.
- Both queries read only narrow columns. The v1 schema puts `request_body`,
  `response_body` and `response_raw` **last**, so a scan touches leading columns on the
  row's own page and never faults in blob overflow pages — the reason no new index is
  warranted at the 10 000-row cap. If a real database ever shows this hot, index
  `model`/`backend` then, with the measurement in hand.
- Limits live beside `SessionLimits` in `Summary.cs`: `ChartLimits.MaxPoints = 5000`,
  `MaxSeries = 6`, `MaxGroups = 50`.

### D4 — Wiring

- `Api/SeriesEndpoint.cs` and `Api/AggregateEndpoint.cs`, in the shape of
  `StatsEndpoint`/`FacetsEndpoint`: static `Handle`, source-gen serialization, no MVC.
- `VesselApp.cs`: map `GET /vessel/api/series` and `GET /vessel/api/aggregate` alongside the
  other `/vessel/api` reads.
- `ApiJsonContext`: add `SeriesResponse`, `SeriesGroup`, `SeriesPoint`, `AggregateResponse`,
  `AggregateRow`.
- Unknown `metric`/`groupBy`/`by` values → `400 invalid_request` via `VesselErrors`, never a
  silent default. (`session` keeps `/requests`' lenient parsing, for consistency.)
- Doc sweep in the same change: architecture.md §7 route table, code.md §4 route table.

---

## 4. Frontend — the shared chart foundation

### D5 — Dependencies

`npm i d3-scale d3-shape` plus `npm i -D @types/d3-scale @types/d3-shape`. `d3-scale`
already brings `d3-time-format` transitively, so time-tick formatting needs no fourth
package — use `scale.tickFormat()`. Nothing else is added: point bisection for the tooltip
is a ten-line binary search, not a reason to pull in `d3-array` directly.

### D6 — Primitives (`components/ui/chart/`)

Per §8.2 these are visual primitives and live under `components/ui`; app-level report cards
only compose them.

- **`ChartFrame.tsx`** — measures its container (`useChartSize`, ResizeObserver), owns
  margins, axes, gridlines, the `<figure>` / `aria-label` / visually-hidden `<table>`
  wrapper, and the empty state. Children render into a translated plot `<g>`.
- **`LineChart.tsx`** — 1–6 series over a time x-axis. One series renders as area (20% mix)
  + line; two or more render as lines only (§2.3).
- **`BarChart.tsx`** — horizontal categorical bars; optional grouped (two measures per key)
  or two-part stacked (ok/failed).
- **`ChartLegend.tsx`** — text label + color swatch per series, reusing the Badge shape.
- **`ChartTooltip.tsx`** — follows the pointer; the §6 popover look (surface, border,
  radius-control, shadow-panel) but not the Popover primitive, which is anchored.
- **`useChartSize.ts`** — the ResizeObserver hook.

Hard rules for all of them:

- **SVG is sized in real pixels** (`width`/`height` from the measured box), not scaled
  through a `viewBox`. Scaling a viewBox distorts stroke widths and text, and is the fastest
  way to lose the dense devtool look (§8.8).
- **Every color is a `var(--color-…)` in a `stroke`/`fill` attribute.** No JS color values
  anywhere — that is what makes a theme flip free, and it is why canvas was rejected (S1).
- Inline `style` only for measured/dynamic geometry, per §8.4.
- Fixed heights from a two-value set: `240` (the full-width time series) and `180` (grid
  cards). No arbitrary heights.
- No layout animation; hover emphasis is opacity only (§7), skipped entirely under
  `prefers-reduced-motion`.

### D7 — Series colors (`lib/chartColors.ts`)

- `groupBy=tag`: color from the existing `tagVariant(tag)` hash, so a tag's line matches its
  pill everywhere (§6's promise). Because two tags can hash to the same hue, a collision
  inside one chart steps to the next unused ramp entry — deterministic, since series arrive
  rank-ordered from the server.
- Any other grouping (`model`, `backend`, `format`) and the single unkeyed series: the ramp
  in order, by rank index.
- `--chart-1..6` are consumed as class or attribute values, never as JS hex strings.

### D8 — Accessibility

Each card renders `<figure aria-label="…">` with a one-sentence summary that includes the
headline value ("Context growth: tokens in per request over time, 3 tags, peak 184,220"),
plus an `sr-only` `<table>` carrying the same rows the chart draws. Group-by controls and
legend entries are real buttons with visible focus. This is the §8.7 floor, not a
nice-to-have.

---

## 5. Frontend — the Reports view

### D10 — The view toggle

`App.tsx` gains `view: 'history' | 'reports'`. The header panel renders the segmented
control (§6 Tabs). In `reports`, the list+detail row is replaced by one full-width panel
(`--surface`, radius-panel, shadow-panel) that scrolls internally — the viewport still never
scrolls (§5). Session scope, stats and every other header control are unchanged.

### D11 — Carrying the filters (S3)

`filters` already lives in `App`, so Reports passes the same object to both queries. Because
the FilterBar is not on screen in this view, `ReportScopeBar.tsx` renders the active filters
at the top of the Reports panel as chips — `model: qwen3:32b`, `tag: planner`, `"timeout"`
for the text query, `status: error`, `warnings only` — each with an × that clears that one
filter, plus **Clear filters**. Clearing here mutates the same state, so switching back to
History shows exactly the scope the charts described. With no active filters the bar renders
the session name alone, not an empty strip.

### D12 — Card inventory

Layout: the context-growth chart full width at 240px, then a two-column grid of 180px cards,
collapsing to one column below ~1100px (§5's collapse rule).

1. **Context growth** (#25) — `series?metric=tokens_in`, with a `None | Tag | Model`
   group-by segmented control, defaulting to `None`. Single-series draws area+line; grouped
   draws lines. Footnote when grouped by tag: *"a request with several tags appears in
   each."* Truncation disclosure whenever `truncated`.
2. **Tokens by model** (#26 acceptance) — `aggregate?by=model`, grouped horizontal bar, in
   vs out.
3. **Tokens by tag** (#26 acceptance) — `aggregate?by=tag`, same form, with the multi-tag
   footnote.
4. **Requests by model** — `aggregate?by=model`, horizontal bar with the failed portion as a
   `--danger` segment (semantic color, per §2.3).
5. **Avg tok/s by model** — `aggregate?by=model`, horizontal bar; `—` for groups with no
   measured rate, never `0`.

Cards 2–5 read from **two** underlying queries (`by=model`, `by=tag`), not five: the endpoint
returns every measure per group, so the cards are projections of one fetch each.

Every card: `xs` uppercase section label, `--surface-2` block at radius-control (matching
§5.2's metric cards), `—` placeholders that keep their slot, and the §6 empty state (muted
mark + one sentence) when the scope has no data. Token totals carry the `~` prefix when
`estimated`.

### D13 — Interaction

- Hover: crosshair plus a tooltip with timestamp, series name and formatted value.
- Click a point on the context-growth chart → switch to `history` with that request selected
  (`{ kind: 'row', id }`). DetailPane fetches by id, so the pane is correct even when the row
  is not in a loaded page; **scroll-to-row is explicitly not built here** — that is a list
  concern and a separate change.
- Bars are not clickable in v1: a bar is a group, not a request.

### D14 — Data, caching, invalidation

- Query keys, added to `api/queryKeys.ts` beside the existing roots:
  `['series', metric, groupBy, scope, filters]` and `['aggregate', by, scope, filters]`.
- `enabled: view === 'reports' && scope !== null`, with `refetchInterval: 5000` — the same
  cadence as `['stats']` and `['sessions']`. The History view pays nothing for Reports
  existing.
- **No SSE patching.** These are aggregates; patching them incrementally on every `completed`
  frame would duplicate the aggregation in the client and be wrong the moment a filter is
  active, while invalidating on each frame would re-run both queries under load. A 5s poll
  while the view is open is the honest, cheap answer.
- **Invalidation sweep** (AGENTS.md — a change is not done until every call site is updated):
  everywhere `['stats']` is invalidated today, the `['series']` and `['aggregate']` roots must
  be invalidated too — `App.handleDeleteSessions`, the clear paths in `DataPanel`, and the
  reset flow.

---

## 6. New / changed files

```
docs/ui-spec.md                             §2.3 added; §2.1/§2.2/§5/§8.5 amended
docs/architecture.md                        §7 route table; §10 Views
docs/code.md                                §4 route table; §5 UI framework
docs/plan.md                                Phase 7 bullets struck through
THIRD-PARTY-NOTICES.md                      d3-scale / d3-shape (ISC)
CHANGELOG.md

src/Vessel/Storage/SqliteReadStore.cs       GetSeries, GetAggregate, tag fan-out
src/Vessel/Storage/Summary.cs               Series*/Aggregate* records, ChartLimits
src/Vessel/Api/SeriesEndpoint.cs            new
src/Vessel/Api/AggregateEndpoint.cs         new
src/Vessel/Api/ApiJsonContext.cs            new serializable types
src/Vessel/VesselApp.cs                     two routes
tests/Vessel.Tests/ChartQueryTests.cs       new (store-level)
tests/Vessel.Tests/ApiTests.cs              endpoint contract cases

frontend/package.json                       d3-scale, d3-shape (+ @types)
frontend/src/index.css                      @theme: --chart-grid/--chart-axis/--chart-1..6
frontend/src/lib/chartColors.ts             new
frontend/src/components/ui/chart/*          ChartFrame, LineChart, BarChart,
                                            ChartLegend, ChartTooltip, useChartSize
frontend/src/components/reports/*           ReportsView, ReportScopeBar,
                                            ContextGrowthCard, AggregateBarCard
frontend/src/api/{types,client,queryKeys}.ts
frontend/src/App.tsx                        view state, toggle, invalidation sweep
frontend/src/components/StatsBar.tsx        the History / Reports control
frontend/src/**/*.test.ts(x)                scales and paths, color assignment,
                                            truncation + multi-tag disclosures, scope bar
```

---

## 7. Batches

| # | Batch | Gate |
|---|---|---|
| B1 | ui-spec §2.3 plus the §2.1/§2.2/§5/§8.5 edits, and the `@theme` tokens. **Docs and tokens only; no chart code.** This is #25's stated blocking sub-step. | ui-spec reads correctly; `vite build` still green |
| B2 | `SqliteReadStore.GetSeries`/`GetAggregate`, `ChartLimits`, tag fan-out in `ConfigureFilteredCommand` | `ChartQueryTests` green, including the `q` + `tag` + `groupBy=tag` three-way case |
| B3 | Endpoints, JSON context, routes, `ApiTests` cases | `dotnet test` |
| B4 | Chart primitives + `chartColors` + their unit tests | `npm run test`, `lint`, `build` |
| B5 | ReportsView, header toggle, scope bar, cards, invalidation sweep | `npm run test`; manual gate (§8) |
| B6 | Doc sweep, notices, CHANGELOG | — |

B1 and B2/B3 are independent; B4 depends on B1, and B5 on B3 + B4.

---

## 8. Verification gate

- `dotnet test` — new store and endpoint tests green; nothing existing weakened.
- `npm run test`, `npm run lint`, `npm run build` in `frontend/`.
- `verify/publish-smoke.ps1` — the bundle grew; confirm the embedded UI still serves and the
  single-file publish is unaffected. Record the new `dist/assets/index-*.js` size in the
  implementation record (expected ≈ +43 KB min over 542 KB).
- **CSP:** load `/vessel/` with the Reports view open and assert zero CSP violations in the
  console. `script-src 'self'` carries no `unsafe-eval`; this is asserted, not assumed (S4).
- **Manual, both themes:** a session with ≥2 tags and ≥2 models — every card renders, the
  legend text matches the line colors, and a tag's chart color equals its row pill.
- A session with 0 requests → every card shows the §6 empty state, never axes with no data.
- A scope past 5 000 matching rows → the truncation disclosure is visible and states both
  numbers.
- Filters: set a model filter in History, switch to Reports — chips show it, the charts
  reflect it, and clearing a chip updates both views.
- Keyboard: tab reaches the view toggle, the group-by control, the legend and every scope
  chip, all with visible focus. `prefers-reduced-motion` removes the hover transitions.

---

## 9. Acceptance

**#25** — for a selected session (optionally grouped by tag), a chart shows `tokens_in`
growth across the run, and ui-spec §2.3 carries the chart tokens and form rules that made it
possible.

**#26** — the Reports view renders tokens-by-model and tokens-by-tag from real captured data
on the same chart foundation, alongside request counts and avg tok/s by model.

---

## 10. Notes for the implementer

- The x-axis is **time**, not request index. An index axis would space bursts evenly and hide
  idle gaps; the issue asks for growth *over time*, and those gaps are signal.
- Nothing here touches the capture path, the writer, or the proxy. Both endpoints are
  read-only, on the read store's own short-lived read-only connections.
- If any part of §3's SQL turns out to need a schema or index change to perform acceptably,
  **stop and report** rather than adding a migration inside this change.

---


---

## 11. Implementation record (2026-08-30)

- **All batches landed.** B1 (tokens) → B2/B3 (store + endpoints, 401/401 backend tests) →
  B4/B5 (primitives, Reports view, 157/157 frontend tests, lint 0 warnings 0 errors,
  `tsc -b` clean) → B6 (this sweep). No capture-path, writer, proxy or schema change was
  needed or made; every §3 query ran on the existing indexes, so nothing hit §10's
  stop-and-report condition.
- **Bundle:** `dist/assets/index-CFD9lxEJ.js` is 600.07 kB min (182.05 kB gzip) against
  the 542 kB baseline — ≈ +58 kB min, above the ≈ +43 kB S1 estimate because d3-scale/
  d3-shape land unbundled-but-whole plus the full Reports surface. `verify/publish-smoke.ps1`
  passed untrimmed win-x64 at 24.8 MB and asserted the published exe serves the new
  bundle (`/vessel/assets/index-CFD9lxEJ.js` loads).
- **`plot.ts` split out of `ChartFrame` (deviation from D6's file list).** oxlint's
  react fast-refresh rule forbids a component file exporting plain values; the margin/
  plot-area geometry and `ChartHeight` moved to `components/ui/chart/plot.ts`, with
  `ChartFrame` re-exporting the types so chart components' import surface stayed stable.
  `seriesLabel` stayed in `LineChart.tsx` but unexported — nothing outside needed it.
- **Number formatting** in card disclosures uses explicit `en-US` (`toLocaleString('en-US')`),
  matching `lib/format.ts`; the one intentional bare `toLocaleString()` left is the
  tooltip/table timestamp, which follows the locale like `formatTimestamp` always has.
- **Requests-by-model card stacks ok/failed** per D12 with `var(--color-danger)` on the
  failed segment only; avg tok/s renders '—' (no bar) for groups with no measured rate,
  never a zero bar.
- **Remaining gates are author-run, browser-bound, and not asserted here:** the CSP
  console check with Reports open (§8), the both-theme legend/pill color pass, the
  keyboard walk, and the 0-request / >5,000-row visual passes. Everything automatable
  about them (disclosure strings, empty states, cap behavior, click-to-select) is covered
  by the store, endpoint, and component tests listed above.

---

## 12. Round 2 — live-use feedback (#25, #26)

Two rounds of live-use comments landed on the shipped batch: a real multi-agent session's
context-growth chart was a dense, unreadable sawtooth (issue #25, two comments), and a
single-model/single-backend scope made three of #26's cards render one meaningless bar
each while two useful reports (cache cost, tail latency) were still missing (issue #26,
one comment). Root cause for #25's first comment: `LineChart` connected points in the
order the API returned them (insertion/id order), not sorted by the time x-axis — under
concurrent multi-agent capture those orders diverge (documented in Summary.cs's own
"`started_at` can tie or skew" caveat), so the drawn line jumped backward across the plot
whenever that happened. That was a genuine bug, fixed and covered by a dedicated test
(`LineChart.test.tsx`'s "draws the line in time order…") before this round's feedback
arrived — the *remaining* messiness the comments describe is real data (interleaved
agents at different context sizes, and agents whose own context-compaction cycles are
visible as a sawtooth), which is exactly what #25 asked the chart to make legible.

### Decisions

| # | Decision | Resolution |
|---|---|---|
| S5 | **Ungrouped chart form.** Confirmed with the user (two options: scatter-only vs. scatter + a computed trend line). | **Scatter only** — no trend line. A regression/trend fit across several interleaved agents' unrelated request sizes would draw a growth rate that doesn't correspond to any real agent, the same reasoning the second #25 comment used to rule out smoothing for the grouped case. |
| S6 | **Small multiples scope.** Confirmed with the user (build now vs. defer as a follow-up). | **Build now.** The comment specified the shape precisely enough (mini-chart per key, shared y-axis, existing card grid) that deferring bought no clarity, only a second review pass. |
| D15 | **Tag-default heuristic.** | Reuses the `by=tag` aggregate fetch `ReportsView` already makes for the "Tokens by tag" card (`hasTags = byTag.data?.rows.some(r => r.key !== null)`) rather than a new query. `undefined` while that fetch is still loading, so the default doesn't flash on before the first real answer arrives; a `useRef` flag means a manual groupBy pick (including picking None back) is never fought once made. |
| D16 | **Legend isolate/hide semantics.** | Plain click isolates (hides every other series; clicking the isolated one again restores all); shift-click keeps the pre-existing single-entry hide/show toggle. Scoped to `ChartLegend`+`LineChart`'s per-key legend only — the aggregate bar cards' legends label *fixed measures* (tokens-in/out, ok/failed), not data-driven series, and isolating "tokens out" has no equivalent meaning. |
| D17 | **Small-multiples layout.** | Its own grid inside the Context Growth card's section, styled identically to the aggregate cards' grid (`grid-cols-1 gap-3 min-[1100px]:grid-cols-2`) — not literally interleaved into the same DOM grid as the token/request bar cards, which would have entangled two independently-scrolling/loading sections for no real benefit. |
| D18 | **Percentile computation.** | A second, narrow query per `GetAggregate` call (`key, duration_ms` pairs, non-null only, same scope/fan-out as the main query), grouped and nearest-rank-indexed in C# — not a SQL window-function emulation of `PERCENTILE_CONT` (SQLite has none bundled). Matches `GetSeries`'s existing fetch-then-group-in-C# style and is bounded by the same ≤10 000-row retention cap the rest of the store already treats as a full-scan budget. Computed uniformly for every `by=` dimension (the per-group data is already being read), not gated behind a flag. |
| D19 | **Warnings dimension shape.** | Reused `/aggregate?by=warning` rather than a new endpoint — `ConfigureFilteredCommand`'s `tagFanOut: bool` generalized to `FanOutColumn { None, Tags, Warnings }`, joining `json_each` over whichever JSON-array column the dimension needs. `AggregateRow`'s existing fields (avg duration, cache columns) are harmless, occasionally useful extras for a warning-code group, not reasons to invent a narrower response shape. |
| D20 | **Cache/duration card data source.** | Both reuse the *existing* `by=model`/`by=tag` fetches (`tokensCachedRead` and the new percentile fields ride every `AggregateRow` already) — no new query for either. Only `Warnings by type` needed a genuinely new dimension. |
| D21 | **Degenerate-grouping collapse.** | Uniform per-card treatment (`data.totalGroups === 1` → a `statLine`, one per projection) rather than "yield its slot" (hiding the card outright) — a compact answer is more informative than a card disappearing, and simpler to reason about than conditional layout. |

### Batches

| # | Batch | Gate |
|---|---|---|
| B7 | Backend: generalized `FanOutColumn`, `by=warning`, nearest-rank percentiles on every `AggregateRow` | `ChartQueryTests`/`ApiTests` additions, full suite green |
| B8 | Frontend: `ChartLegend` isolate/hide, `LineChart` scatter mode + `yDomainMax` + `showLegend`, `ContextGrowthSmallMultiples` | component tests, `tsc -b`, lint |
| B9 | `ContextGrowthCard` tag-default + Overlay/Grid wiring, `AggregateBarCard` collapse + `cache`/`duration` projections, `ReportsView` new cards | component tests, full suite green, live browser check |
| B10 | Doc sweep (this section, ui-spec §2.3/§5, architecture/code.md route tables, CHANGELOG) | — |

### Implementation record (2026-09-01)

- **All batches landed.** Backend: 405/405 tests (402 prior + 3 new: percentile nearest-
  rank incl. null-duration exclusion, `by=warning` fan-out at the store level, and over
  HTTP). Frontend: 173/173 tests (162 prior + 11 across `LineChart`/`ContextGrowthCard`/
  new `AggregateBarCard.test.tsx`), lint 0 warnings/errors, `tsc -b` clean, build green
  (603.62 kB min / 183.03 kB gzip, ≈ +4 kB over the phase-7 baseline for round 2's added
  components — no new npm dependency).
- **Live-verified, not just unit-tested:** the id/time-order bug fix was confirmed against
  a deliberately-skewed seeded database (two interleaved synthetic agents whose insertion
  order and start time diverge every round, mimicking real concurrent capture) — 0
  out-of-order pairs across 120 points in the rendered chart, versus the reported sawtooth
  before the fix.
- **No schema migration.** The warnings fan-out is a `json_each` join exactly like the
  existing tag fan-out; percentiles are computed in C#, not a new column.

---

## 13. Round 3 — live-use feedback (single-model, untagged sessions)

Live use on an ordinary single-model chat session (no tags, one model) surfaced that
round 2's degenerate-collapse rule (§12/D21) wasn't quite right: `Tokens`/`Requests`/`Avg
tok/s by model|tag`, once collapsed to their single group, restate a number the header
stats bar already shows verbatim — a stat line there added nothing but a repeated total.
Proposed a "collapse into one merged summary card" fix first; the user correctly pointed
out that a summary card would *also* just restate the header, with the sole exception of
`Duration by tag`'s p50/p95 (never shown anywhere else). That reframed the fix from "make
the collapse denser" to "distinguish cards that duplicate the header from cards that
don't" — the header-duplicating ones drop entirely, the rest render, but as a nicer
`StatPanel` (round 3's other ask: the plain-sentence collapse "looked like plain text",
requested to look "more like a stat panel").

### Decisions

| # | Decision | Resolution |
|---|---|---|
| D22 | **Which cards drop vs. collapse.** | Drop entirely (not even a stat line) when the fetch resolves to one group: `Tokens by model`, `Tokens by tag`, `Requests by model`, `Requests by tag`, `Avg tok/s by model` — every one of these mirrors a header stat exactly once there's only one group. Keep, as a `StatPanel`: `Duration by tag` (p50/p95, not on the header), `Cache efficiency` (the cached-% ratio; the header has the two raw numbers but not their ratio), `Warnings by type` (the code breakdown itself). |
| D23 | **Stat panel visual form.** | Not prose. The group's own name/key once, then its numbers as `--surface-3` tiles (one level deeper than the card's own `--surface-2`) — xs uppercase label over a mono value, deliberately echoing the header's own `Stat` component (StatsBar.tsx) so a collapsed card reads as *a smaller stat bar*, not an afterthought sentence next to its bar-chart siblings. Sized below `text-stat`, which ui-spec §3 already reserves for the header alone. |
| D24 | **Loading-state ordering.** | `ReportsView`'s `showByModelBreakdown`/`showByTagBreakdown` default to `true` while their fetch is still in flight (`data === undefined`), only flipping to hide once `totalGroups` is known to be 1 — so a card doesn't render, then vanish, once data settles; it simply never appears if it was never going to. |

### Implementation record (2026-09-01)

- **Batch landed:** `AggregateBarCard`'s `statLine: (row, fmt) => string` became
  `statFields: (row, fmt) => StatField[]`, rendered by a new `StatPanel` component;
  `ReportsView` gates the five header-duplicating card instances on their fetch's own
  `totalGroups`. Frontend: 176/176 tests (174 prior + a rewritten degenerate-collapse
  assertion, a new "omits the failed tile" case, and a new `ReportsView.test.tsx` pinning
  the drop/keep split), lint clean, `tsc -b` clean, build green (604.49 kB min / 183.21 kB
  gzip). No backend change — this round is presentation-only, over data the `/aggregate`
  endpoint already returns.
- **Live-verified** against two seeded scopes: a 13-request single-model/untagged session
  (only `Context growth`, `Duration by tag`, `Warnings by type` render; the five
  header-duplicating cards are absent from the DOM, not just visually) and the existing
  423-request multi-tag/multi-model scope (all eight cards still render as bar charts,
  confirming the gate doesn't misfire when there's real dimension diversity).
