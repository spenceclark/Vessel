/**
 * Phase 7 D6/D8 — text label + color swatch per series, reusing the Badge shape (pill,
 * xs, 14% tinted fill). Entries with an onToggle are real buttons with visible focus
 * (§8.7); dimmed marks the hidden state.
 *
 * #25 live-use feedback (round 2) — "meaningful alone, meaningless together": a plain
 * click *isolates* the clicked series (hides every other one), clicking the same entry
 * again restores all of them; shift-click keeps the older single-entry hide/show toggle,
 * for pulling one noisy series out without losing the rest. `onToggle` reports which of
 * the two the click was, so the chart (which owns the hidden-set state) can implement
 * either without this component knowing about series count or visibility semantics.
 */
export function ChartLegend({
  entries,
  onToggle,
}: {
  entries: { label: string; colorVar: string; dimmed?: boolean }[]
  onToggle?: (index: number, mode: 'isolate' | 'hide') => void
}) {
  return (
    // role="group" — a plain div's aria-label isn't reliably exposed by assistive tech;
    // a labelable landmark role is what actually surfaces "Series" to screen readers.
    <div className="flex flex-wrap items-center gap-1.5" role="group" aria-label="Series">
      {entries.map((entry, index) => {
        const swatch = (
          <svg width={8} height={8} aria-hidden="true">
            <circle cx={4} cy={4} r={4} fill={entry.colorVar} />
          </svg>
        )
        return onToggle ? (
          <button
            key={entry.label}
            type="button"
            aria-pressed={!entry.dimmed}
            title="Click to isolate this series, Shift-click to hide just it"
            onClick={(event) => onToggle(index, event.shiftKey ? 'hide' : 'isolate')}
            className={`inline-flex h-6 items-center gap-1.5 rounded-full border border-border px-2.5 py-0.5 text-xs transition-opacity hover:bg-surface-2 ${
              entry.dimmed ? 'opacity-40' : ''
            }`}
          >
            {swatch}
            <span className="text-text-secondary">{entry.label}</span>
          </button>
        ) : (
          <span
            key={entry.label}
            className="inline-flex items-center gap-1.5 rounded-full border border-border px-2.5 py-0.5 text-xs"
          >
            {swatch}
            <span className="text-text-secondary">{entry.label}</span>
          </span>
        )
      })}
    </div>
  )
}
