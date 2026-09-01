import { memo, type ReactNode } from 'react'
import { Mark } from '@/components/ui/Mark'
import { cn } from '@/lib/utils'
import { useChartSize } from './useChartSize'
import { DEFAULT_MARGINS, plotArea, type ChartHeight, type Margins, type PlotSize } from './plot'

// Type-only re-export keeps the chart components' import surface stable.
export type { ChartHeight }

/** The visually-hidden data table every chart carries (§2.3 accessibility floor). */
export interface ChartTable {
  columns: string[]
  rows: (string | number | null)[][]
}

export interface Tick {
  position: number
  label: string
}

/**
 * Split out and memoized: LineChart's hover state changes on every pointermove, which
 * would otherwise re-run this (up to MaxPoints × MaxSeries rows) on every mouse move even
 * though the chart's data hasn't changed. `table`/`label` are stable across hover-only
 * re-renders, so the default shallow-prop comparison skips this entirely in that case.
 */
const ChartAccessibleTable = memo(function ChartAccessibleTable({
  label,
  table,
}: {
  label: string
  table: ChartTable
}) {
  return (
    <table className="sr-only">
      <caption>{label}</caption>
      <thead>
        <tr>
          {table.columns.map((column) => (
            <th key={column} scope="col">{column}</th>
          ))}
        </tr>
      </thead>
      <tbody>
        {table.rows.map((row, index) => (
          <tr key={index}>
            {row.map((cell, cellIndex) => (
              <td key={cellIndex}>{cell ?? '—'}</td>
            ))}
          </tr>
        ))}
      </tbody>
    </table>
  )
})

/**
 * Phase 7 D6 — the shared chart chrome: container measurement, margins, gridlines and
 * axis labels (all token-colored), the <figure> / aria-label / sr-only <table> wrapper
 * (§8.7), and the §6 empty state. Children render into the translated plot <g> and
 * receive the measured plot area; tick labels arrive already formatted via lib/format.
 */
export function ChartFrame({
  label,
  height,
  table,
  empty = false,
  emptyText = 'No data in this scope.',
  margins,
  xTicks = [],
  yTicks = [],
  children,
  overlay,
}: {
  label: string
  height: ChartHeight
  table: ChartTable
  empty?: boolean
  emptyText?: string
  margins?: Partial<Margins>
  xTicks?: Tick[]
  yTicks?: Tick[]
  children?: (plot: PlotSize) => ReactNode
  overlay?: ReactNode
}) {
  const [ref, size] = useChartSize()
  const m: Margins = { ...DEFAULT_MARGINS, ...margins }
  const plot: PlotSize = plotArea(size.width, height, m)

  return (
    <figure aria-label={label} className="relative m-0">
      {empty ? (
        <div className={cn('flex w-full flex-col items-center justify-center gap-2', height === 240 ? 'h-[240px]' : 'h-[180px]')}>
          <Mark size={28} muted />
          <p className="text-sm text-text-muted">{emptyText}</p>
        </div>
      ) : (
        <div ref={ref} className={cn('w-full', height === 240 ? 'h-[240px]' : 'h-[180px]')}>
          {size.width > 0 && (
            <svg width={size.width} height={height} className="block">
              <g transform={`translate(${m.left},${m.top})`}>
                {/* Gridlines are --chart-grid: deliberately weaker than a panel border,
                    so a chart never reads as a table. Axis chrome is --chart-axis. */}
                {yTicks.map((tick) => (
                  <line
                    key={`grid-y-${tick.position}`}
                    x1={0}
                    x2={plot.width}
                    y1={tick.position}
                    y2={tick.position}
                    stroke="var(--color-chart-grid)"
                  />
                ))}
                <line x1={0} x2={plot.width} y1={plot.height} y2={plot.height} stroke="var(--color-chart-axis)" />
                {yTicks.map((tick) => (
                  <text
                    key={`label-y-${tick.position}`}
                    x={-6}
                    y={tick.position}
                    dy="0.32em"
                    textAnchor="end"
                    className="fill-chart-axis text-xs tabular-nums"
                  >
                    {tick.label}
                  </text>
                ))}
                {xTicks.map((tick) => (
                  <text
                    key={`label-x-${tick.position}`}
                    x={tick.position}
                    y={plot.height + 15}
                    textAnchor="middle"
                    className="fill-chart-axis text-xs tabular-nums"
                  >
                    {tick.label}
                  </text>
                ))}
                {plot.width > 0 && children?.(plot)}
              </g>
            </svg>
          )}
          {overlay}
        </div>
      )}
      {/* §8.7 — the same data the chart draws, for screen readers; the legend is text, never color alone. */}
      <ChartAccessibleTable label={label} table={table} />
    </figure>
  )
}