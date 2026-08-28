# Vessel — UI Specification & Design System

> **This document is the design authority for everything under `frontend/`.** Phase
> specs defer to it for look and feel the way they defer to architecture.md for
> backend design. Any phase that adds UI follows these rules; any deliberate deviation
> is recorded here, in place, like phase-spec findings. If a mock and this doc
> disagree, this doc wins until it's amended.

Vessel is a developer tool that sits open all day next to terminals and editors. The
look should read as **calm, dense, precise** — closer to a well-made devtool (Linear,
the Vite docs, a good log viewer) than a marketing dashboard. Nothing decorative that
isn't also informative; polish comes from spacing, alignment, and restraint, not
gradients and glow.

---

## 1. Identity

### 1.1 Name & wordmark

Product name renders lowercase: **vessel**. Wordmark = the word set in the UI font,
`font-weight: 650`, `letter-spacing: -0.02em`, foreground color, with the mark to its
left. No taglines in the app chrome.

### 1.2 The mark (logo)

A code-drawn inline SVG (no image assets, no external files — it ships in the bundle
and doubles as the favicon):

- A **rounded-rectangle vessel** (the container) with a **flow line** passing through
  it: a horizontal line entering from the left edge, continuing inside the vessel as a
  brighter segment, and exiting the right edge — traffic passing through, observed.
- Geometry: 24×24 viewBox; vessel = rect x4 y5 w16 h14, rx4, stroke 2, no fill; flow
  line = y12, from x0→x4 and x20→x24 in `--text-muted`, and x7→x17 inside the vessel
  in `--accent`, stroke 2, round caps. Optionally a 2px accent dot at x12 y12
  (the captured request).
- The mark must stay legible at 16×16 (favicon: same SVG, vessel stroke in
  `#94b8c8`-ish neutral, inner flow line in the accent — served as `favicon.svg`).
- Usage: mark + wordmark in the header panel, mark alone in the favicon and empty
  states. Never stretched, recolored per-context, or given a shadow.

---

## 2. Color

Dark is the primary theme (the audience lives in dark terminals); light is fully
supported via the same token set and `prefers-color-scheme` (no toggle yet — a toggle
is a later, cheap addition *because* everything goes through tokens).

Base neutrals are slate-cool (a touch of blue, never brown/warm); the accent is
**teal-cyan** — nautical without being on the nose, and distinct from the blue/purple
every AI tool defaults to. Semantic colors are muted, not neon: this UI shows walls
of red-adjacent data (errors, warnings) and must stay restful.

### 2.1 Token set (the complete palette — no color exists outside this table)

| Token | Dark | Light | Use |
|---|---|---|---|
| `--canvas` | `#0b0d10` | `#eef1f4` | page background behind panels |
| `--surface` | `#14171c` | `#ffffff` | panel background |
| `--surface-2` | `#1b1f26` | `#f5f7f9` | nested surfaces: inputs, code blocks, hover, chips |
| `--surface-3` | `#232936` | `#e9edf1` | active/selected fills, pressed states |
| `--border` | `#262c36` | `#dde3e9` | panel and control borders |
| `--border-strong` | `#39414f` | `#c3ccd6` | focused-adjacent, dividers that must read |
| `--text` | `#e6eaf0` | `#171c23` | primary text |
| `--text-secondary` | `#a8b1bd` | `#4b5563` | labels, secondary values |
| `--text-muted` | `#6b7684` | `#8b95a1` | placeholders, timestamps, de-emphasis |
| `--accent` | `#2dd4bf` | `#0d9488` | brand: links, active tab, focus ring, selection, in-flight pulse |
| `--accent-fg` | `#0b0d10` | `#ffffff` | text on solid accent fills |
| `--ok` | `#4ade80` | `#16a34a` | 2xx status dot, success |
| `--danger` | `#f87171` | `#dc2626` | errors, failed, destructive buttons |
| `--warn` | `#fbbf24` | `#b45309` | warning badges |
| `--info` | `#7dd3fc` | `#0369a1` | informational badges (estimated tokens, usage-injected) |
| `--tag-blue` | `#60a5fa` | `#2563eb` | tag pill color 1 of 6 (picked by hash, §9.1) |
| `--tag-indigo` | `#818cf8` | `#4f46e5` | tag pill color 2 of 6 |
| `--tag-violet` | `#a78bfa` | `#7c3aed` | tag pill color 3 of 6 |
| `--tag-pink` | `#f472b6` | `#db2777` | tag pill color 4 of 6 |
| `--tag-fuchsia` | `#e879f9` | `#a21caf` | tag pill color 5 of 6 |
| `--tag-steel` | `#94a3b8` | `#475569` | tag pill color 6 of 6 |

The tag ramp deliberately excludes red, amber, green, cyan-sky, and teal — those hues
are claimed by `--danger`/`--warn`/`--ok`/`--info`/`--accent`, and a tag pill must
never be mistakable for a status at a glance. (An earlier `--tag-orange` violated
this — it rendered warn-adjacent next to real warning badges in live traffic and was
replaced by fuchsia + steel.)

Tinted fills (badge backgrounds, hover washes) are **derived**, never new hexes:
`color-mix(in srgb, var(--warn) 14%, transparent)` — the percentages allowed are
10/14/20/30. That rule alone is what keeps forty future badge variants coherent.

### 2.2 Usage rules

- Meaning is never carried by color alone: every colored badge/dot has text or an
  accessible label next to it.
- `--danger` text on `--surface` must stay ≥ 4.5:1 contrast in both themes (the
  values above do); don't lighten tokens ad hoc — change the token or don't.
- The accent is for *interaction and identity*, not data. Metrics are neutral;
  status uses the semantic trio; charts (Phase 5) get their own tokens added here
  first.

---

## 3. Typography

Two families, both **bundled via npm** (`@fontsource-variable/inter`,
`@fontsource-variable/jetbrains-mono`) — no CDN fonts ever (the UI must work
air-gapped from a single binary). System stacks as fallback.

| Token | Stack | Use |
|---|---|---|
| `--font-ui` | `InterVariable, system-ui, sans-serif` | everything by default |
| `--font-mono` | `"JetBrains Mono Variable", ui-monospace, Consolas, monospace` | paths, models, ids, headers, JSON, code, all metric *values* |

Scale (UI is deliberately dense — this is a data tool):

| Step | Size/line | Use |
|---|---|---|
| `xs` | 11px/16px | timestamps, badge text, section labels (labels also `uppercase, tracking 0.06em, --text-muted, weight 550`) |
| `sm` | 12.5px/18px | **the default**: rows, values, controls, tabs |
| `base` | 14px/20px | detail-pane message text, markdown body |
| `lg` | 16px/22px | panel titles (rare) |
| `stat` | 20px/24px, weight 600 | the header panel's stat values only |

Rules: numbers that sit in columns or update live always get `font-variant-numeric:
tabular-nums`. Weight vocabulary is 400/500/600 (650 for the wordmark) — no 700+.
Mono for anything a developer might copy.

---

## 4. Space, shape, depth

- **Spacing**: 4px base grid. Panel padding 16; between panels 12; between controls 8;
  inside every badge/chip (`Badge`, any variant) 4×10, pill-roomy (§6 — a pill needs
  more room than a tight status tag to read as one; this used to be tag-only, see
  §9.1). No arbitrary values like 13px — if a Tailwind spacing utility can't express
  it, it's wrong.
- **Radius**: `--radius-panel: 12px` (the three panels, dialogs), `--radius-control:
  8px` (buttons, inputs, tabs, cards inside panels), `--radius-chip: 6px` (the method
  chip, tab segments — no longer `Badge`, see §9.1), `9999px` for status dots, the
  in-flight pulse, and every `Badge` (all variants, not just tags).
- **Borders**: 1px `--border` on every panel and control that has a fill. No 2px
  borders; emphasis comes from `--border-strong` or a fill change.
- **Shadows**: exactly two. `--shadow-panel: 0 1px 2px rgb(0 0 0 / .3), 0 4px 16px
  rgb(0 0 0 / .15)` (panels, popovers — halve alphas in light theme) and
  `--shadow-dialog` (a deeper variant for modals). Nothing glows.
- **Depth model**: canvas → panel (`--surface`) → nested block (`--surface-2`) →
  selected (`--surface-3`). Never stack a shadow inside a panel; inner structure is
  borders and fills only.

---

## 5. Layout — the overhaul

The app stops being full-bleed. It becomes **panels floating on a canvas**:

```
┌─ canvas (--canvas, fills viewport) ───────────────────────────────┐
│   ┌─ header panel ────────────────────────────────────────────┐   │
│   │ ⌂ vessel │ stat │ stat │ stat │ stat │ stat │  ⋯ controls │   │
│   └───────────────────────────────────────────────────────────┘   │
│   ┌─ list panel (420px) ─┐  ┌─ detail panel (flex) ───────────┐   │
│   │ search + filters     │  │ tabs                            │   │
│   │ virtualized rows     │  │ content                         │   │
│   └──────────────────────┘  └─────────────────────────────────┘   │
└───────────────────────────────────────────────────────────────────┘
```

- **Max width 1600px, centered**, 16px gutter (24px ≥1280px). On a 3440px monitor the
  app is a composed object, not wallpaper. Below ~1100px the two columns may collapse
  (list above detail) — fine to defer; never horizontal-scroll the page.
- The **viewport never scrolls**. Panels scroll internally (list rows; detail tab
  content). Header panel and the list panel's filter area are always visible.
- **Header panel** (was the full-width StatsBar): a real panel — mark + wordmark on
  the left; stat group center-left; session scope toggle, Reset, gear, backend
  indicator right. Stats render as label-over-value pairs (`xs` label, `stat` value)
  separated by hairline dividers, not a run-on text line.
- **List panel**: search + filter controls live *inside* the panel as its header
  (own bottom border), rows below. Selected row = `--surface-3` fill + 2px accent
  inset bar on the left edge; hover = `--surface-2`.
- **Detail panel**: tab strip as panel header, content scrolls. Empty state: centered
  mark (muted) + one line ("Select a request").

### 5.1 Row anatomy (list)

Two lines, 8×12 padding. The hierarchy is tuned for the real scanning questions —
*who (tag), ok?, how long/fast* — because in a typical session method, path, and
model repeat on every row and carry little scanning value:

- **Line 1:** status dot (8px) · method chip (mono `xs`, `--surface-2`) · path
  (mono `sm`, middle-out truncation) · *right:* the metric columns.
- **Metric columns, not a cluster:** duration and tok/s are two **fixed-width
  right-aligned sub-columns** (mono `xs`, tabular-nums) so values align vertically
  down the list — durations under durations, rates under rates. Units render in
  `--text-muted` (`3.04s` dim-`s`, `47.0` dim-`tok/s`) so the digits carry the
  emphasis.
- **Line 2:** **tag pills lead** (categorical color, §2.1/§6) — the left edge of
  line 2 is the who-column when scanning multi-agent traffic. Model follows in
  `xs --text-muted` (demoted: it's context, not identity). *Right:* warning-count
  badge. Rows without tags: model leads the line in `xs --text-muted`.
- More than two tags: first two pills + a neutral `+n` chip (full set in detail
  Overview).
- Status dot leads line one; in-flight rows keep the accent dot with a 1.2s opacity
  pulse (the only looping animation in the app) + running timer, no metric columns
  until `first_token`/completion supply them.

### 5.2 Detail Overview

Metrics become a **card grid** (2–3 columns of `--surface-2`, radius-control cards):
each card = `xs` label + mono value (+ muted unit). Groups: Request · Timing · Tokens
(· Rate limits when present). Warnings render as a badge row above the grid. em-dash
placeholders (`—`) keep their slot — absent data is information. A card's value can
flag `--danger` instead of `--text` for a bad-outcome metric: Status (4xx/5xx or an
error string), Truncated (`true`), and Stop reason when it's one of the error-class
values (`content_filter`, `refusal`, `error`) rather than a normal completion
(`stop`, `length`, `end_turn`, …).

---

## 6. Components (canonical looks)

- **Buttons**: default = `--surface-2` fill, 1px border, radius-control, `sm`,
  padding 6×12; primary = accent fill + `--accent-fg` (one per view max);
  destructive = danger-tinted (14% mix) with danger text; ghost = borderless for
  icon buttons. Hover shifts fill one step; active adds inset. No size zoo: one
  height (28px), plus a 24px icon-button.
- **Badges/chips**: pill-shaped (`9999px`), `xs`, 4×10 padding, 10–14% tinted fill +
  colored text — one shape for every `Badge` variant (§9.1: this used to be
  tag-only, with status badges kept tighter at radius-chip/2×8; unified so a warning
  badge sitting next to a tag pill doesn't read as a visually different, smaller
  class of thing). Vocabulary: warnings → `--warn`; errors → `--danger`; info-class
  (`tokens_estimated`, `usage_injected`) → `--info`; format/method → neutral mono.
  Tags are still the one *categorical* (not status) variant — colored by hashing the
  tag string against the `--tag-*` tokens (§2.1, §9.1) so a given tag name always
  renders the same color everywhere it appears: row, detail Overview, filter picker.
  A filter picker chip that's currently the active filter shows the interaction/
  selected look (`--accent` fill, §2.2) instead of its tag color. Warning codes
  render as short human labels (map lives in `lib/warnings.ts`), never raw
  snake_case.
- **Tabs**: segmented control — `--surface-2` track (radius-control), active segment
  `--surface` fill + border + `--text`; inactive `--text-secondary`. No underline
  tabs anywhere.
- **Inputs**: `--surface-2` fill, border, radius-control, 28px, `sm`; placeholder
  `--text-muted`; focus per §7. Search inputs get a leading 14px icon.
- **Dialogs**: radius-panel, `--surface`, `--shadow-dialog`, 420px default width,
  backdrop `rgb(0 0 0 / .5)`; title `lg`, body `sm`. Destructive confirmations keep
  the typed-confirmation pattern.
- **Scrollbars**: styled thin (8px, `--border-strong` thumb, transparent track,
  `border-radius: 4px`) via `::-webkit-scrollbar` + `scrollbar-width: thin` — default
  chrome scrollbars inside rounded panels are the fastest way to look unfinished.
- **Code/JSON blocks**: `--surface-2`, border, radius-control, mono `sm`, 12px
  padding, max-height with internal scroll. (PrettyJson and `.md pre` both conform.)
- **Empty states**: muted mark + one `sm` sentence + optionally one action. Never a
  bare "No data".

---

## 7. Motion & focus

- Transitions: `150ms ease` on `background-color, border-color, opacity, transform`
  only. Nothing animates layout. No entrance animations for rows/panels; live rows
  simply appear (the pulse is the liveness cue).
- The in-flight pulse and the dialog fade (100ms) are the only keyframe animations.
  Everything respects `prefers-reduced-motion: reduce` (pulse → static accent dot).
- Focus: `:focus-visible` only — 2px `--accent` ring, 2px offset, radius follows the
  control. Never `outline: none` without the replacement in the same rule. All
  interactive elements are real `<button>`/`<a>`/inputs (rows are buttons).

---

## 8. CSS architecture (rules for every future phase)

1. **Tokens are law.** Every color, font, radius, and shadow in §§2–4 is defined
   once in `index.css` under Tailwind v4's `@theme` (which makes them available as
   utilities: `bg-surface`, `text-muted`, `rounded-panel`, `shadow-panel`) plus the
   `prefers-color-scheme` light overrides. Components use token utilities or
   `var(--…)` — **never raw palette utilities (`bg-zinc-800`, `text-blue-400`) and
   never hex values**. A new color/radius/shadow = a new token in this doc first,
   then in `@theme`.
2. **Layering.** `components/ui/*` (primitives: button, badge, tabs, dialog, input)
   own all primitive styling; app components (`RequestRow`, `DetailPane`, …) only
   compose primitives and layout utilities. If an app component needs a new visual
   primitive, it goes in `components/ui`, styled per §6.
3. **`index.css` stays small**: `@import 'tailwindcss'`, font imports, `@theme`,
   theme overrides, base element rules, scrollbars, and the `.md` markdown block.
   No per-component CSS files, no CSS modules, no styled-anything — utilities +
   `cn()` merging, variants via component props (the existing shadcn-style pattern).
4. **No inline `style=` except dynamic values** (virtualizer transforms, measured
   sizes). If it's static, it's a class.
5. **Dependencies:** UI libraries beyond the current set (react-markdown, TanStack,
   lucide) require updating this doc first. Icons are lucide only — 14px in rows and
   chips, 16px in buttons and tabs, `stroke-width={1.75}`, always with an
   `aria-label` when the icon stands alone.
6. **Numbers:** format via `lib/format.ts` only — durations `1.19s`/`984ms`, rates
   one decimal (`61.6 tok/s`), token counts thousands-separated, timestamps
   `HH:mm:ss` (full date only in Overview). Metric columns: mono + tabular-nums.
   Missing = `—`, never blank, never `0`.
7. **Accessibility floor:** 4.5:1 for text, 3:1 for `xs` labels and icons; visible
   focus everywhere; `aria-live="polite"` on the stats bar's updating values;
   dialogs trap focus and close on Escape (existing Dialog primitive owns this).
8. **Density is a feature.** New views default to `sm`/compact spacing. If a view
   feels empty, tighten the container (§5's max-width), don't inflate the type.
9. **Every new phase-N UI item** names the §6 component it's built from; a phase
   that invents a look updates this doc in the same change (same rule as
   architecture deviations).

---

## 9. Implementation notes for the overhaul pass

- One PR-sized change: rewrite `index.css` (tokens per §2–4 into `@theme`, fonts,
  scrollbars), add the mark/favicon, restructure `App.tsx` per §5 (canvas +
  max-width + three panels), then sweep components to the token utilities and §6
  looks. The old ad-hoc vars (`--background`, `--card`, `--muted`, …) are **removed,
  not aliased** — a sweep must touch every `var(--…)` usage or the build of stale
  names would silently keep working.
- `npm i @fontsource-variable/inter @fontsource-variable/jetbrains-mono`; import
  both in `main.tsx` (they bundle into the embedded dist; adds roughly 150–400KB —
  acceptable, and verify the publish smoke still passes).
- Favicon: replace Vite's default with `favicon.svg` per §1.2 (`<link rel="icon"
  type="image/svg+xml">`).
- Behavior does not change in the overhaul pass: no component logic, API, or test
  changes — style-layer only, verified by `tsc` + `vite build` + eyeballing the
  manual-gate flows from phases 3/4.
- After the overhaul, screenshot header/list/detail in both themes and spot-check
  §8.7's contrast floor (browser devtools' contrast checker is fine).

### 9.1 Overhaul findings (recorded in place, per §8's own rule for deviations)

- **Tailwind v4 token namespacing**: every §2 color lives under `--color-*` in
  `@theme` (`--color-surface`, `--color-text-muted`, …) so the utility classes §8.1
  names (`bg-surface`, `text-muted`) actually resolve — Tailwind's `text-*` scale
  namespace is reserved for font sizes, not colors, so the doc's bare token names
  (`--surface`, `--text-muted`) needed the `color-` prefix to become real utilities.
  Same idea for `--font-*`/`--radius-*`/`--shadow-*`. No visual effect, just how the
  tokens are wired.
- **`--shadow-dialog`** wasn't given an exact value beyond "a deeper variant" —
  landed on `0 4px 8px rgb(0 0 0 / .35), 0 16px 48px rgb(0 0 0 / .3)` (dark),
  halved alphas in light, matching `--shadow-panel`'s halving rule.
- **FilterBar's tag picker doesn't use Badge's status vocabulary** — a selected tag
  is a toggle/interaction state, not a data status, so per §2.2 ("accent is for
  interaction and identity, not data") it's styled directly (`bg-accent` when
  selected) rather than through `Badge`'s `neutral/warn/danger/info` variants.
  `Badge` itself carries the status vocabulary; the warnings-only filter's warn-tint
  when active is a deliberate exception (naming the toggle the same color as what
  it filters), not a data-status use.
- **Overview's "Rate limits" card group** (§5.2) renders one card per limit/
  remaining/reset value per rate-limit category, not one card per category — keeps
  every card in "xs label + mono value," consistent with the rest of the grid,
  rather than inventing a multi-value card shape.
- **No `ui/select.tsx` primitive** — `<select>` elements (backend/model/format
  filters, backend type) get the same visual treatment as `Input` via shared
  utility classes rather than a dedicated component. Native selects have limited
  stylable surface area and the spec doesn't call out a distinct Select look; worth
  promoting to a real primitive if a phase ever needs to style the open dropdown
  itself.
- **Dialog's Escape-to-close + focus trap**: §8.7 names this as the existing
  primitive's job. It wasn't actually implemented before this pass (Phase 4 shipped
  backdrop-click-to-close only) — added both (plus focus-on-open and
  focus-restore-on-close) to `ui/dialog.tsx`, shared by `Dialog` and `ConfirmDialog`.
  Verified live: Escape closes and returns focus to the triggering element.
- **Tags as pills**: the original overhaul left tags in `Badge`'s default
  radius-chip shape along with every other badge variant. Post-launch feedback asked
  for tags specifically to read as pills, to distinguish "a label the request was
  tagged with" from "a status the system computed" at a glance — the one badge
  vocabulary member that isn't a data-status color. Applied via `className="rounded-
  full"` on the `Badge` (Tailwind-merge overrides the base `rounded-chip`) everywhere
  a tag renders: `RequestRow`/`InFlightRow`'s tag chips, the detail Overview Tags
  section, and `FilterBar`'s tag picker. §4 and §6 above updated in place rather than
  logged as a pure deviation, since this is now the standing rule, not a one-off.
- **Tags get a categorical color, not just a pill shape**: follow-up feedback on the
  pill change asked for tags to also carry distinct colors, hashed from the tag
  string so a given name is stable everywhere it renders. This is a new kind of
  color use the original spec didn't anticipate — §2.2 says "the accent is for
  interaction and identity, not data; status uses the semantic trio," which covers
  data-status color but not this: a tag name isn't a status, it's closer to an
  identity a developer assigns, so a small **categorical** palette (5 hues:
  `--tag-blue/-indigo/-violet/-pink/-orange`, §2.1) was added specifically kept off
  the red/amber/green/cyan/teal hues already claimed by danger/warn/ok/info/accent,
  so a tag pill is never mistaken for a status at a glance. Hash is `lib/tags.ts`
  (djb2 mod 5); `Badge` gained 5 `tag-*` variants using the same tint-fill pattern as
  `warn`/`danger`/`info`. FilterBar's tag picker (which bypasses `Badge` entirely,
  above) reuses the same tint classes for its unselected state, so the picker's
  colors match the pills it filters — selected still overrides to `--accent`, since
  that's the interaction state, not the tag's identity. Verified live: same tag name
  produces the same color across the row list, detail Overview, and filter picker,
  in both themes.
- **Stop reason → danger classification**: not something the original spec called
  out (it predates the Overview card grid having a `danger` prop at all). Added a
  small `ERROR_STOP_REASONS` set (`content_filter`, `refusal`, `error`) in
  `DetailPane.tsx` — OpenAI's content filter and Anthropic's refusal, plus a generic
  `error` value — distinct from `length`/`max_tokens`, which is already flagged via
  the separate Truncated card and is a normal (if incomplete) completion, not a
  failure. Local Ollama traffic doesn't produce an error-class stop reason, so this
  was verified by mechanism (the shared `danger` prop path on `MetricCard`, confirmed
  rendering exact `--danger` on the Status card) rather than against a live example;
  worth a real screenshot once a backend that emits one of these values is exercised.
- **Row anatomy v2 + tag-ramp amendment (post-overhaul review, implemented)**: a
  review of the shipped list against live multi-agent traffic found the row
  hierarchy inverted for that workload — method/path/model repeat identically on
  every row while the actual scanning key (the tag) sat last on line 2 — and found
  `--tag-orange` rendering warn-adjacent next to real warning badges, violating the
  ramp's own keep-off-semantic-hues rule. §5.1 rewritten in place (tag pills lead
  line 2; model demoted to `xs --text-muted`; duration/tok-s become fixed-width
  aligned sub-columns with muted units — `lib/format.ts` gained `splitMetric()` to
  separate the digits from the unit for the dimming; `+n` overflow for >2 tags,
  `RequestRow`/`InFlightRow` both slice to 2 + a neutral overflow `Badge`) and the
  ramp amended in §2.1: `--tag-orange` removed, `--tag-fuchsia` + `--tag-steel`
  added (6 hues; `lib/tags.ts` is now djb2 mod 6, `Badge` gained the two variants).
  As predicted, a given tag's color did shift under the new modulus — in the
  verification data both seeded tags (`demo`, `manual-test`) landed on `tag-violet`
  under mod 6, where they'd differed under mod 5; not a bug, just the hash. Verified
  live: fixed-width metric columns align with dimmed units and full-color digits,
  tag pills lead line 2 with model demoted after them (or leading alone when a row
  has no tags), and the `--tag-fuchsia`/`--tag-steel` tokens resolve to the correct
  hex in both themes (exercising an actual tag landing in either bucket was not
  observed against this seed data, since the two available tags collided into the
  same bucket — the mapping itself is code-reviewed and structurally identical to
  the four already-confirmed colors, but call this mechanism-verified, not
  eyeballed, for those two specifically).
- **All badges unified to the pill shape** (implemented, separate from the TODO
  above): live feedback on the tag pill change noticed a status badge
  (`Client disconnected`, a warning) sitting next to a tag pill in Overview read as
  a visibly smaller, different class of element — same information density, but the
  tag/status distinction was accidentally also a size distinction, which wasn't the
  intent. Rather than keep two badge sizes, `Badge`'s base shape (`ui/badge.tsx`)
  moved to pill/4×10 for every variant (`neutral`/`warn`/`danger`/`info`/`tag-*`
  alike); `radius-chip` no longer applies to `Badge` at all (it's still used
  directly by the method chip and tab segments, untouched). Tags remain visually
  distinct from status badges by *color* (categorical hash vs. the semantic trio)
  rather than by shape. §4 and §6 above updated in place. Verified live: a warning
  badge (`HTTP error`) and a tag pill now compute to the identical `4px 10px`
  padding and pill radius, in both themes.
- **In-flight rows: real model, real click target (review TODO, implemented)**: live
  traffic showed two confusions. (a) The in-flight row rendered the *backend* name in
  the model position (the `started` event carries no model — it fires before the
  request body is read), which read as a model called "ollama". Fix: the SSE
  contract gained a fourth event, `request_ready {seq, model}` (contract change
  recorded in phase-3.md D5) — emitted once the request body is fully read, parsed
  from the already-captured request buffer off the request path; until it arrives
  the row shows the backend as its own `Badge` chip and the model slot stays empty.
  (b) In-flight rows looked like rows but didn't click. Fix: they're now real
  buttons — a lightweight client-side in-flight detail (`InFlightDetailPane`: method,
  path, backend, tags, started-at, live elapsed, model once `request_ready` lands,
  and a state line: "waiting for first token…" → "streaming — TTFT 984ms"). All the
  data already lived in the `inFlight` map — no new REST endpoint — but the map
  itself, and the SSE subscription that owns it, moved up from `RequestList` to
  `App` so the detail pane can read the same live state the list renders from;
  `RequestList` now takes `inFlight`/`now`/`selection` as props instead of owning
  them. On `completed`, selection hands over from `seq` to the row id (`App`'s new
  `Selection` union) so the full `DetailPane` replaces the lightweight one in place.
  A *live response tail* in that state line is deliberately out of scope — it
  touches the proxy hot path and is planned as a Phase 5 item (plan.md).
  Verified live: `request_ready` populates the model slot on a real in-flight row
  (backend shown as a distinct chip alongside it, never confused for the model);
  clicking an in-flight row shows the lightweight detail with the correct state line;
  and on completion the pane hands over to the full detail without ever reverting to
  the empty "select a request" state.

  One implementation finding not anticipated above: a background `Task.Run` per
  request for the `request_ready` parse raced `first_token` under load (proven by a
  flaky integration test, not guesswork — traced with temporary logging until the
  actual interleaving was visible) — a warm loopback connection can answer in under
  a millisecond, faster than the thread pool can schedule the parse's continuation,
  and faster than any real backend's TTFT ever is. Fixed by giving `request_ready` a
  dedicated always-running consumer (`RequestModelSnifferService`, mirroring
  `CaptureChannel`/`CaptureWriterService`'s shape) instead of a fresh `Task.Run` each
  time, which removed the thread-pool-scheduling variable from the race entirely.
- **Header panel: session token totals (review TODO, implemented)**: the stats bar
  answers speed and health but not consumption — added total tokens for the scoped
  session. Backend: `GET /stats` gained `tokensIn`, `tokensOut`, `tokensCachedRead`,
  `tokensCachedWrite` (SQL `SUM`s over the same scope as the existing aggregates,
  `COALESCE(...,0)` — never null) plus `tokensEstimated: bool` (`COALESCE(MAX(...),
  0)` — true when *any* contributing row was estimated; contract note in
  phase-3.md D3). Display rules:
  - Two always-present stat slots, **TOKENS IN** and **TOKENS OUT**, after AVG TTFT.
    Compact notation via `lib/format.ts`'s new `formatCompactTokenCount` (`847`,
    `12.4k`, `1.2M` — one decimal, thousands unabbreviated below 10k) — reuses the
    existing `Stat` component verbatim, so it's tabular-nums like every other stat
    value (no separate mono treatment; none of the others have one either).
  - When `tokensEstimated`, prefix both values with `~` (the §8.6 estimate marker) —
    totals mixing exact and estimated rows must not present as exact.
  - **CACHED** appears as a third slot only when read+write > 0 in scope — the
    conditional-slot exception to §5.2's em-dash rule is deliberate: header space
    is the scarcest in the app, and for pure-Ollama sessions (the primary user) a
    permanent "—" slot is noise. In the slot, `read` and `write` render as
    `12.4k r · 310 w` with the letters muted; omit a zero half.
  - Zero rows in scope → `0`, not `—` (the session exists; its total is genuinely
    zero — different from a metric that wasn't measured).

  Verified live: an Anthropic-shaped response with `cache_read_input_tokens: 12400`
  and `cache_creation_input_tokens: 310` renders the CACHED slot as exactly
  `12.4k r · 310 w`; a response with no cache fields omits the slot entirely; a
  response with no `usage` object at all (forcing estimation) renders both totals
  `~`-prefixed.
- **Rendered view: pretty-print JSON-only text blocks (review TODO, not yet
  implemented)**: structured-output workloads (Responses API `text.format`
  json-schema, "reply in JSON" prompting) produce assistant messages whose entire
  text is one line of JSON — faithful, but unreadable crammed into a markdown block.
  In the message-view extractors (all formats, request and response sides): when a
  text/markdown block's trimmed content parses as a JSON object or array, render it
  as a code block (§6 code/JSON look) pretty-printed with 2-space indent instead of
  markdown. Whole-block only — never rewrite JSON embedded *within* prose, and the
  Raw JSON toggle still shows the untouched wire text. Presentation only: no change
  to stored data, `response_text` flattening, or FTS.
