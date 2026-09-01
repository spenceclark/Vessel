import { useMemo } from 'react'
import { scaleBand, scaleLinear } from 'd3-scale'
import { truncateMiddle } from '@/lib/format'
import { ChartFrame, type ChartTable } from './ChartFrame'
import { DEFAULT_MARGINS, plotArea, type ChartHeight, type Margins } from './plot'
import { useChartSize } from './useChartSize'

/** Wider left margin than the default: bar rows carry their (truncated) key label. */
const BAR_MARGINS: Margins = { ...DEFAULT_MARGINS, left: 118, top: 6, bottom: 22 }

export interface BarMeasure {
  name: string
  colorVar: string
}

/**
 * Phase 7 D6/D12 — the horizontal categorical bar. `grouped` draws one sub-bar per
 * measure (in vs out); `stacked` draws the measures end-to-end in one bar (ok/failed,
 * with --danger carrying the semantic failed portion). A null value draws no bar and the
 * sr-only table shows an em-dash — never a zero bar (D13).
 */
export function BarChart({
  rows,
  values,
  measures,
  mode,
  height,
  label,
  formatValue,
  emptyText = 'No requests in scope.',
}: {
  /** Rank-ordered top-first, exactly as they display. */
  rows: { key: string | null }[]
  /** rows × measures; null = no measured value (no bar, "—" in the table). */
  values: (number | null)[][]
  measures: BarMeasure[]
  mode: 'grouped' | 'stacked'
  height: ChartHeight
  label: string
  formatValue: (v: number) => string
  emptyText?: string
}) {
  const [containerRef, containerSize] = useChartSize()
  const empty = rows.length === 0
  const plot = plotArea(containerSize.width, height, BAR_MARGINS)

  const { xTicks, yPositions, xScale } = useMemo(() => {
    const indices = rows.map((_, index) => index)
    const maxGrouped = Math.max(...values.flat().map((v) => v ?? 0), 0)
    const maxStacked = Math.max(
      ...rows.map((_, index) => values[index]!.reduce<number>((sum, v) => sum + (v ?? 0), 0)),
      0,
    )
    if (rows.length === 0 || plot.width === 0) {
      return { xTicks: [], yPositions: [], xScale: null }
    }

    const band = scaleBand<number>()
      .domain(indices)
      .range([0, plot.height])
      .padding(0.25)
    const x = scaleLinear()
      .domain([0, mode === 'grouped' ? maxGrouped : maxStacked])
      .range([0, plot.width])
      .nice()
    // §8.6/§2.3 — the value axis routes through the card's own formatValue so tick labels
    // agree with the bars' own tooltips/table (e.g. "8.1M", not d3's raw "8,000,000").
    return {
      xTicks: x.ticks(4).map((d) => ({ position: x(d), label: formatValue(d) })),
      yPositions: indices.map((index) => ({
        y: band(index) ?? 0,
        height: band.bandwidth(),
      })),
      xScale: x,
    }
  }, [rows, values, mode, plot.width, plot.height, formatValue])

  const table: ChartTable = {
    columns: ['Key', ...measures.map((measure) => measure.name)],
    rows: rows.map((row, index) => [
      row.key ?? '(none)',
      ...measures.map((_, measureIndex) => {
        const value = values[index]![measureIndex]
        return value === null ? null : formatValue(value)
      }),
    ]),
  }

  return (
    <div ref={containerRef} className="w-full">
      <ChartFrame
        height={height}
        label={label}
        table={table}
        empty={empty}
        emptyText={emptyText}
        margins={BAR_MARGINS}
        xTicks={xTicks}
      >
        {(plotSize) =>
          xScale ? (
            <>
              {rows.map((row, index) => {
                const rowValues = values[index]!
                const y = yPositions[index]!.y
                const rowHeight = yPositions[index]!.height
                return (
                  <g key={row.key ?? '(none)'}>
                    <text
                      x={-6}
                      y={y + rowHeight / 2}
                      dy="0.32em"
                      textAnchor="end"
                      className="fill-chart-axis text-xs"
                    >
                      <title>{row.key ?? '(none)'}</title>
                      {truncateMiddle(row.key ?? '(none)', 15)}
                    </text>
                    {mode === 'grouped'
                      ? measures.map((measure, measureIndex) => {
                          const value = rowValues[measureIndex]
                          if (value === null) return null
                          const subHeight = rowHeight / measures.length
                          return (
                            <rect
                              key={measure.name}
                              x={0}
                              y={y + measureIndex * subHeight}
                              width={xScale(value)}
                              height={Math.max(1, subHeight - 2)}
                              rx={1}
                              fill={measure.colorVar}
                            >
                              <title>{`${measure.name}: ${formatValue(value)}`}</title>
                            </rect>
                          )
                        })
                      : (() => {
                          let cursor = 0
                          return measures.map((measure, measureIndex) => {
                            const value = rowValues[measureIndex]
                            if (value === null) return null
                            const x0 = cursor
                            cursor += xScale(value)
                            return (
                              <rect
                                key={measure.name}
                                x={x0}
                                y={y}
                                width={xScale(value)}
                                height={rowHeight}
                                rx={1}
                                fill={measure.colorVar}
                              >
                                <title>{`${measure.name}: ${formatValue(value)}`}</title>
                              </rect>
                            )
                          })
                        })()}
                  </g>
                )
              })}
              <line
                x1={0}
                x2={0}
                y1={0}
                y2={plotSize.height}
                stroke="var(--color-chart-axis)"
              />
            </>
          ) : null
        }
      </ChartFrame>
    </div>
  )
}