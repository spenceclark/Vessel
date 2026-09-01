import { useMemo, useState } from 'react'
import { scaleLinear, scaleTime } from 'd3-scale'
import { area, line } from 'd3-shape'
import type { SeriesPoint } from '@/api/types'
import { ChartFrame, type ChartTable } from './ChartFrame'
import { DEFAULT_MARGINS, plotArea, type ChartHeight } from './plot'
import { ChartLegend } from './ChartLegend'
import { ChartTooltip } from './ChartTooltip'
import { useChartSize } from './useChartSize'

/**
 * Phase 7 D5 — the line/area chart on d3-scale + d3-shape. Single visible series renders
 * as area + line (§2.3: overlapping fills are unreadable); several render as lines.
 * The x-axis is time, not request index — bursts must not space evenly and hide idle
 * gaps, which are signal. Hover is a crosshair plus a tooltip; clicking a point selects
 * the request. All colors are var() attributes, never JS color values.
 */
export interface LineSeries {
  key: string | null
  /** Oldest-first by id, as the series endpoint returns them — re-sorted by time internally. */
  points: SeriesPoint[]
}

function seriesLabel(key: string | null): string {
  return key ?? '(none)'
}

/** §2.3 — series fills are derived, never new colors: the 20% sanctioned tint. */
function seriesFill(colorVar: string): string {
  return `color-mix(in srgb, ${colorVar} 20%, transparent)`
}

/** Ten-line binary search over time-sorted points (D5: no d3-array dependency). */
function nearestPointByTime(points: SeriesPoint[], time: number): SeriesPoint {
  let lo = 0
  let hi = points.length - 1
  while (hi - lo > 1) {
    const mid = (lo + hi) >> 1
    if (Date.parse(points[mid]!.t) < time) lo = mid
    else hi = mid
  }
  const a = points[lo]!
  const b = points[hi]!
  return Math.abs(Date.parse(a.t) - time) <= Math.abs(Date.parse(b.t) - time) ? a : b
}

export function LineChart({
  series: rawSeries,
  colors,
  height,
  label,
  formatValue,
  formatTime,
  onSelectPoint,
  emptyText = 'No requests with this metric in scope.',
  renderMode = 'line',
  yDomainMax,
  showLegend = true,
}: {
  series: LineSeries[]
  /** Per-series ramp color (var(--color-chart-N)), same order — see assignSeriesColors. */
  colors: string[]
  height: ChartHeight
  /** The one-sentence §8.7 summary, composed by the card that knows the scope. */
  label: string
  formatValue: (v: number) => string
  formatTime: (iso: string) => string
  onSelectPoint?: (id: number) => void
  emptyText?: string
  /**
   * #25 live-use feedback — an ungrouped "None" series draws unrelated interleaved
   * requests; connecting them with a line manufactures a trend that isn't real. Scatter
   * (dots, no line/area) is the honest form for that case. Grouped views (tag/model) keep
   * 'line': each series there IS one thing growing over time.
   */
  renderMode?: 'line' | 'scatter'
  /**
   * Small-multiples support — force the y-axis domain instead of deriving it from this
   * chart's own data, so several mini line charts (one per key) stay visually comparable.
   */
  yDomainMax?: number
  /** Small multiples supply the key as their own card title; a per-mini-chart legend of one entry is redundant. */
  showLegend?: boolean
}) {
  const [containerRef, containerSize] = useChartSize()
  // The API's points are oldest-first *by id* (insertion order) — documented as
  // "started_at can tie or skew". That skew isn't an edge case here: under concurrent
  // multi-agent traffic, a slower call can start first but finish (and so get written)
  // later than several faster concurrent calls, so id-order and time-order genuinely
  // diverge. The x-axis is time, and both the line/area path (drawn point-to-point in
  // array order) and the nearest-point search below assume ascending time — so points
  // are re-sorted by time once, per series, before either runs. Without this, the path
  // jumps backward across the plot every time a point lands out of insertion order, and
  // the tooltip's "nearest point" can be the wrong one.
  const series = useMemo(
    () => rawSeries.map((s) => ({ ...s, points: [...s.points].sort((a, b) => Date.parse(a.t) - Date.parse(b.t)) })),
    [rawSeries],
  )
  const [hidden, setHidden] = useState<ReadonlySet<number>>(() => new Set())
  const [hover, setHover] = useState<{ x: number; y: number; seriesIndex: number; point: SeriesPoint } | null>(null)

  const empty = series.length === 0 || series.every((s) => s.points.length === 0)
  const visible = series
    .map((s, index) => ({ series: s, color: colors[index]!, index }))
    .filter(({ index }) => !hidden.has(index))

  const plot = plotArea(containerSize.width, height)

  const { xTicks, yTicks, xScale, yScale } = useMemo(() => {
    const times = series.flatMap((s) => s.points.map((p) => Date.parse(p.t)))
    const values = series.flatMap((s) => s.points.map((p) => p.v))
    if (times.length === 0 || plot.width === 0) {
      return { xTicks: [], yTicks: [], xScale: null, yScale: null }
    }

    const min = Math.min(...times)
    const max = Math.max(...times)
    // A single point would collapse the time domain; pad it so the dot has an axis.
    const x = scaleTime()
      .domain(min === max ? [min - 1_000, max + 1_000] : [min, max])
      .range([0, plot.width])
    const y = scaleLinear()
      .domain([0, yDomainMax ?? Math.max(...values, 0)])
      .range([plot.height, 0])
      .nice()
    // Time ticks use d3's own adaptive formatter (§2.3 rule doesn't cover time-axis
    // formatting; lib/format.ts has no adaptive tick precision for that). Value ticks route
    // through the card's own formatValue so the axis agrees with the tooltip and table —
    // both are the same "format via lib/format.ts" surface (§8.6).
    const xTickFormat = x.tickFormat()
    return {
      xTicks: x.ticks(5).map((d) => ({ position: x(d) as number, label: xTickFormat(d) })),
      yTicks: y.ticks(4).map((d) => ({ position: y(d), label: formatValue(d) })),
      xScale: x,
      yScale: y,
    }
  }, [series, plot.width, plot.height, formatValue, yDomainMax])

  const lineGenerator = yScale
    ? line<SeriesPoint>().x((p) => xScale!(Date.parse(p.t))).y((p) => yScale(p.v))
    : null
  const areaGenerator = yScale
    ? area<SeriesPoint>().x((p) => xScale!(Date.parse(p.t))).y0(plot.height).y1((p) => yScale(p.v))
    : null

  // The sr-only table carries the rows the chart draws (§8.7) — the visible series.
  const table: ChartTable = {
    columns: ['Series', 'Time', 'Value'],
    rows: visible.flatMap(({ series: s }) =>
      s.points.map((p) => [seriesLabel(s.key), formatTime(p.t), formatValue(p.v)]),
    ),
  }

  function handleMove(event: React.PointerEvent<Element>) {
    if (!xScale || !yScale || visible.length === 0) return
    // `currentTarget` is the hit-rect *inside* ChartFrame's <g transform="translate(margins)">,
    // so its own bounding rect already sits at the plot origin — no extra margin subtraction.
    const rect = event.currentTarget.getBoundingClientRect()
    const px = event.clientX - rect.left
    if (px < 0 || px > plot.width) {
      setHover(null)
      return
    }

    const time = xScale.invert(px).getTime()
    let best: { index: number; point: SeriesPoint; distance: number } | null = null
    for (const { series: s, index } of visible) {
      const point = nearestPointByTime(s.points, time)
      const distance = Math.abs(xScale(Date.parse(point.t)) - px)
      if (best === null || distance < best.distance) best = { index, point, distance }
    }
    if (best === null) {
      setHover(null)
      return
    }

    setHover({
      x: xScale(Date.parse(best.point.t)),
      y: yScale(best.point.v),
      seriesIndex: best.index,
      point: best.point,
    })
  }

  // #25 live-use feedback (round 2) — click isolates the clicked series (hides every
  // other one); clicking it again while isolated restores all of them. Shift-click keeps
  // the plain single-entry hide/show toggle, for pulling one noisy series out without
  // losing the rest.
  function handleLegendToggle(index: number, mode: 'isolate' | 'hide') {
    setHidden((previous) => {
      if (mode === 'hide') {
        const next = new Set(previous)
        if (next.has(index)) next.delete(index)
        else next.add(index)
        return next
      }

      const isolatedToThis = previous.size === series.length - 1 && !previous.has(index)
      if (isolatedToThis) return new Set()
      return new Set(series.map((_, i) => i).filter((i) => i !== index))
    })
  }

  return (
    <div ref={containerRef} className="w-full">
      <ChartFrame
        height={height}
        label={label}
        table={table}
        empty={empty}
        emptyText={emptyText}
        xTicks={xTicks}
        yTicks={yTicks}
        overlay={
          hover && (
            <ChartTooltip
              x={Math.min(hover.x + DEFAULT_MARGINS.left + 12, Math.max(0, containerSize.width - 180))}
              y={Math.min(hover.y + 10, Math.max(0, height - 76))}
              title={formatTime(hover.point.t)}
              rows={[
                {
                  label: seriesLabel(series[hover.seriesIndex]!.key),
                  value: formatValue(hover.point.v),
                  colorVar: colors[hover.seriesIndex],
                },
              ]}
            />
          )
        }
      >
        {(plotSize) =>
          yScale ? (
            <>
              {renderMode === 'line' && visible.length === 1 && areaGenerator && (
                <path
                  d={areaGenerator(visible[0]!.series.points) ?? undefined}
                  fill={seriesFill(visible[0]!.color)}
                  stroke="none"
                />
              )}
              {renderMode === 'line' &&
                lineGenerator &&
                visible.map(({ series: s, color }) => (
                  <path
                    key={seriesLabel(s.key)}
                    d={lineGenerator(s.points) ?? undefined}
                    fill="none"
                    stroke={color}
                    strokeWidth={1.5}
                    strokeLinejoin="round"
                    strokeLinecap="round"
                  />
                ))}
              {renderMode === 'scatter' &&
                visible.flatMap(({ series: s, color }) =>
                  s.points.map((p) => (
                    <circle
                      key={`${seriesLabel(s.key)}-${p.id}`}
                      cx={xScale!(Date.parse(p.t))}
                      cy={yScale(p.v)}
                      r={2.5}
                      fill={color}
                      fillOpacity={0.75}
                    />
                  )),
                )}
              {hover && (
                <>
                  <line
                    x1={hover.x}
                    x2={hover.x}
                    y1={0}
                    y2={plotSize.height}
                    stroke="var(--color-chart-axis)"
                  />
                  <circle
                    cx={hover.x}
                    cy={hover.y}
                    r={3.5}
                    fill={colors[hover.seriesIndex]}
                    stroke="var(--color-surface)"
                    strokeWidth={1.5}
                  />
                </>
              )}
              <rect
                x={0}
                y={0}
                width={plotSize.width}
                height={plotSize.height}
                fill="transparent"
                onPointerMove={handleMove}
                onPointerLeave={() => setHover(null)}
                onClick={() => hover && onSelectPoint?.(hover.point.id)}
              />
            </>
          ) : null
        }
      </ChartFrame>
      {showLegend && (
        <div className="mt-2">
          <ChartLegend
            entries={series.map((s, index) => ({
              label: seriesLabel(s.key),
              colorVar: colors[index]!,
              dimmed: hidden.has(index),
            }))}
            onToggle={handleLegendToggle}
          />
        </div>
      )}
    </div>
  )
}
