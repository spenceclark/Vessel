/**
 * Phase 7 D6 — the pointer-following chart tooltip. The §6 popover look (surface, border,
 * radius-control, shadow-panel) but not the Popover primitive, which is anchored to a
 * trigger; this follows the pointer. Colors ride the series ramp via fill attributes.
 * `role="tooltip"` rather than `"status"` — a live region re-announces on every pointer
 * move across the chart; the sr-only table (§8.7) already carries the same data for
 * screen-reader users, so this is a sighted-pointer affordance only.
 */
export interface ChartTooltipRow {
  label: string
  value: string
  colorVar?: string
}

export function ChartTooltip({
  x,
  y,
  title,
  rows,
}: {
  /** Position relative to the chart container, already clamped by the caller. */
  x: number
  y: number
  title: string
  rows: ChartTooltipRow[]
}) {
  return (
    <div
      role="tooltip"
      className="pointer-events-none absolute z-10 min-w-32 rounded-control border border-border bg-surface px-2.5 py-1.5 shadow-panel"
      style={{ left: x, top: y }}
    >
      <div className="text-xs font-[550] text-text-secondary">{title}</div>
      {rows.map((row) => (
        <div key={row.label} className="mt-0.5 flex items-center gap-1.5 text-xs">
          {row.colorVar && (
            <svg width={8} height={8} aria-hidden="true">
              <circle cx={4} cy={4} r={4} fill={row.colorVar} />
            </svg>
          )}
          <span className="text-text-muted">{row.label}</span>
          <span className="ml-auto pl-3 font-mono tabular-nums text-text">{row.value}</span>
        </div>
      ))}
    </div>
  )
}