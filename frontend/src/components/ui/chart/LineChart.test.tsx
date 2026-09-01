import { createElement } from 'react'
import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { SeriesPoint } from '@/api/types'
import { CHART_RAMP } from '@/lib/chartColors'
import { LineChart } from './LineChart'

/**
 * Phase 7 D5/D6 — the shared line chart: one series renders area + line, several render
 * lines only (§2.3), the sr-only table carries the drawn rows (§8.7), hover picks the
 * nearest point and a click selects it (D13). jsdom has no ResizeObserver; the stub
 * reports a fixed 800px container, the same box the scale math runs against.
 */

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
})

class ResizeObserverStub {
  private readonly callback: ResizeObserverCallback
  constructor(callback: ResizeObserverCallback) {
    this.callback = callback
  }
  observe(): void {
    this.callback(
      [{ contentRect: { width: 800, height: 240, x: 0, y: 0, top: 0, left: 0, bottom: 240, right: 800 } } as ResizeObserverEntry],
      this as unknown as ResizeObserver,
    )
  }
  unobserve(): void {}
  disconnect(): void {}
}

/**
 * A `ResizeObserver` whose `observe()` never calls back — real `ResizeObserver`'s first
 * notification for a newly observed target is itself asynchronous, so a fast unmount
 * before that callback ever gets a chance to run is a real possibility (a card's data
 * flipping empty→populated→empty→populated across a query-key change, faster than the
 * browser's next rendering opportunity). Pairs with mocking `getBoundingClientRect` below
 * to isolate whether a chart renders from `useChartSize`'s *synchronous* attach-time read
 * alone, with the observer contributing nothing.
 */
class SilentResizeObserverStub {
  observe(): void {}
  unobserve(): void {}
  disconnect(): void {}
}

function point(id: number, t: string, v: number): SeriesPoint {
  return { id, t, v }
}

function renderChart(overrides: Partial<Parameters<typeof LineChart>[0]> = {}) {
  const onSelectPoint = vi.fn()
  render(
    createElement(LineChart, {
      series: [{ key: null, points: [point(1, '2026-08-31T09:00:00Z', 10), point(2, '2026-08-31T09:01:00Z', 30)] }],
      colors: [CHART_RAMP[0]!],
      height: 240,
      label: 'Context growth: tokens in per request over time.',
      formatValue: (v: number) => String(v),
      formatTime: (iso: string) => iso,
      onSelectPoint,
      ...overrides,
    }),
  )
  return onSelectPoint
}

describe('LineChart (phase 7 D5/D6)', () => {
  it('renders one area + one line for a single series, with the accessibility wrapper', () => {
    vi.stubGlobal('ResizeObserver', ResizeObserverStub)
    renderChart()

    const figure = screen.getByRole('figure', { name: 'Context growth: tokens in per request over time.' })
    const paths = figure.querySelectorAll('path')
    const area = Array.from(paths).find((p) => p.getAttribute('fill')?.startsWith('color-mix(in srgb, var(--color-chart-1) 20%'))
    const line = Array.from(paths).find((p) => p.getAttribute('stroke') === CHART_RAMP[0])
    expect(area).toBeTruthy()
    expect(line).toBeTruthy()
    // The d3 line path visits both points, in real pixels (no viewBox scaling).
    const d = line!.getAttribute('d')!
    expect(d.startsWith('M')).toBe(true)
    expect(d).toContain('740,0') // the second point sits at the plot's right edge
  })

  it('renders lines only for several series — overlapping fills are unreadable', () => {
    vi.stubGlobal('ResizeObserver', ResizeObserverStub)
    renderChart({
      series: [
        { key: 'a', points: [point(1, '2026-08-31T09:00:00Z', 10)] },
        { key: 'b', points: [point(2, '2026-08-31T09:01:00Z', 20)] },
      ],
      colors: [CHART_RAMP[0]!, CHART_RAMP[1]!],
    })

    const figure = screen.getByRole('figure')
    const paths = Array.from(figure.querySelectorAll('path'))
    expect(paths.filter((p) => p.getAttribute('stroke') !== null)).toHaveLength(2)
    expect(paths.filter((p) => p.getAttribute('fill')?.startsWith('color-mix'))).toHaveLength(0)
  })

  it('carries an sr-only table of the same rows the chart draws', () => {
    vi.stubGlobal('ResizeObserver', ResizeObserverStub)
    renderChart()

    const table = screen.getByRole('table', { hidden: true })
    expect(table.querySelectorAll('thead th')).toHaveLength(3)
    expect(table.querySelectorAll('tbody tr')).toHaveLength(2)
    expect(table.textContent).toContain('2026-08-31T09:01:00Z')
    expect(table.textContent).toContain('30')
  })

  it('hover picks the nearest point and click selects its request id', () => {
    vi.stubGlobal('ResizeObserver', ResizeObserverStub)
    const onSelectPoint = renderChart()

    // The hit-rect sits inside ChartFrame's <g transform="translate(46,10)">, so its own
    // client rect left is the plot's real on-screen x (46 for an 800px container with the
    // default margins) — jsdom doesn't compute SVG transforms into getBoundingClientRect,
    // so it's stubbed here to the value a real browser would report. Plot spans x 46..786;
    // the second point (09:01) sits at the right edge, so pointer clientX 746 (px 700 in
    // plot space) lands nearest it.
    const overlay = document.querySelector('svg rect[fill="transparent"]')! as SVGRectElement
    vi.spyOn(overlay, 'getBoundingClientRect').mockReturnValue({
      left: 46, top: 10, right: 786, bottom: 250, width: 740, height: 240, x: 46, y: 10,
      toJSON: () => '',
    })
    fireEvent.pointerMove(overlay, { clientX: 746, clientY: 10 })
    fireEvent.click(overlay, { clientX: 746, clientY: 10 })

    expect(onSelectPoint).toHaveBeenCalledWith(2)
    // The tooltip announces the hovered series and value.
    expect(screen.getByRole('tooltip').textContent).toContain('30')
  })

  it('does not double-subtract the plot margin from the pointer position', () => {
    vi.stubGlobal('ResizeObserver', ResizeObserverStub)
    const onSelectPoint = renderChart()

    // Same real-world rect as above (left: 46). A pointer at the plot's left edge, in
    // page coordinates, is clientX 46 — px 0. The old double-subtraction formula would
    // have computed px = 46 - 46 - 46 = -46 (off the left edge) and reported no hover at
    // all; the fix must land on the first point instead.
    const overlay = document.querySelector('svg rect[fill="transparent"]')! as SVGRectElement
    vi.spyOn(overlay, 'getBoundingClientRect').mockReturnValue({
      left: 46, top: 10, right: 786, bottom: 250, width: 740, height: 240, x: 46, y: 10,
      toJSON: () => '',
    })
    fireEvent.pointerMove(overlay, { clientX: 46, clientY: 10 })
    fireEvent.click(overlay, { clientX: 46, clientY: 10 })

    expect(onSelectPoint).toHaveBeenCalledWith(1)
  })

  it('draws the line in time order even when points arrive out of order', () => {
    vi.stubGlobal('ResizeObserver', ResizeObserverStub)
    // The API's points are oldest-first *by id* (insertion order), documented as
    // "started_at can tie or skew" — under concurrent multi-agent traffic that skew is
    // routine: a slower call can start first but finish (and get written) after several
    // faster concurrent ones. Fed straight to the array-order line generator, this drew a
    // path that jumped backward across the plot every time a point landed out of
    // insertion order — the "sawtooth" a real multi-agent session reported live.
    renderChart({
      series: [{
        key: null,
        points: [
          point(1, '2026-08-31T09:02:00Z', 10), // chronologically last, first in the array
          point(2, '2026-08-31T09:00:00Z', 20), // chronologically first, second in the array
          point(3, '2026-08-31T09:01:00Z', 30), // chronologically middle, last in the array
        ],
      }],
    })

    const line = document.querySelector(`svg path[stroke="${CHART_RAMP[0]}"]`)!
    const xs = Array.from(line.getAttribute('d')!.matchAll(/[ML](-?[\d.]+),/g)).map((m) => Number(m[1]))
    expect(xs).toHaveLength(3)
    // Drawn left-to-right by time (09:00 → 09:01 → 09:02), not in the out-of-order array.
    expect(xs[0]).toBeLessThan(xs[1]!)
    expect(xs[1]).toBeLessThan(xs[2]!)
  })

  // A real ResizeObserver's first notification for a newly observed target is itself
  // asynchronous. A card whose data flips empty→populated across a query-key change (the
  // context-growth card switching group-by) can attach, then detach, then reattach a
  // *fresh* container div faster than that first callback ever gets a chance to run —
  // stuck blank forever under the old mount-once-effect implementation, and still exposed
  // even under a callback ref if attach relied solely on the observer's own async
  // callback. The chart must render from `useChartSize`'s synchronous attach-time
  // `getBoundingClientRect` read alone; this stub's observer never calls back at all.
  it('renders immediately on a fresh mount even if the observer never calls back', () => {
    vi.stubGlobal('ResizeObserver', SilentResizeObserverStub)
    const rectSpy = vi.spyOn(Element.prototype, 'getBoundingClientRect').mockReturnValue({
      width: 800, height: 240, top: 0, left: 0, right: 800, bottom: 240, x: 0, y: 0, toJSON: () => '',
    })

    const { rerender } = render(
      createElement(LineChart, {
        series: [], // empty — no container div mounts yet
        colors: [],
        height: 240,
        label: 'Context growth: tokens in per request over time.',
        formatValue: (v: number) => String(v),
        formatTime: (iso: string) => iso,
      }),
    )
    // The empty state's Mark is an svg too; what must be absent is the actual chart svg
    // (ChartFrame's own, marked with its "block" class — Mark carries no className).
    expect(document.querySelector('figure svg.block')).toBeNull()

    // Data arrives (simulating the query resolving after a group-by change): the
    // container div mounts for the first time, on this later render.
    rerender(
      createElement(LineChart, {
        series: [{ key: null, points: [point(1, '2026-08-31T09:00:00Z', 10), point(2, '2026-08-31T09:01:00Z', 30)] }],
        colors: [CHART_RAMP[0]!],
        height: 240,
        label: 'Context growth: tokens in per request over time.',
        formatValue: (v: number) => String(v),
        formatTime: (iso: string) => iso,
      }),
    )

    // No await, no timer flush — if this is present, it came from the synchronous read.
    expect(document.querySelector('figure svg')).not.toBeNull()
    expect(document.querySelectorAll('figure svg path[stroke]').length).toBeGreaterThan(0)

    rectSpy.mockRestore()
  })

  it('shows the empty state, not axes, when there is no data', () => {
    vi.stubGlobal('ResizeObserver', ResizeObserverStub)
    renderChart({ series: [], colors: [] })

    expect(screen.getByText('No requests with this metric in scope.')).toBeTruthy()
    // The empty state's mark is an svg too — what must be absent is the axis chrome.
    expect(document.querySelector('line[stroke="var(--color-chart-axis)"]')).toBeNull()
  })

  // #25 live-use feedback — the ungrouped view draws unrelated interleaved requests;
  // connecting them with a line manufactures a trend that isn't real. Scatter mode must
  // draw only points, never a connecting line or area fill.
  it('does not crash hovering when a visible series has zero points', () => {
    // D1: an empty points array is a legal series shape (e.g. a group with no requests in
    // the drawn window). The nearest-point search and hover loop must skip it, not index
    // into an empty array.
    vi.stubGlobal('ResizeObserver', ResizeObserverStub)
    renderChart({
      series: [
        { key: 'a', points: [point(1, '2026-08-31T09:00:00Z', 10)] },
        { key: 'b', points: [] },
      ],
      colors: [CHART_RAMP[0]!, CHART_RAMP[1]!],
    })

    const overlay = document.querySelector('svg rect[fill="transparent"]')! as SVGRectElement
    vi.spyOn(overlay, 'getBoundingClientRect').mockReturnValue({
      left: 46, top: 10, right: 786, bottom: 250, width: 740, height: 240, x: 46, y: 10,
      toJSON: () => '',
    })
    expect(() => fireEvent.pointerMove(overlay, { clientX: 400, clientY: 10 })).not.toThrow()
    expect(screen.getByRole('tooltip').textContent).toContain('10')
  })

  it('renders a zero-only series without collapsing the y-scale to NaN', () => {
    // A zero-width [0, 0] domain divides by zero in d3's linear scale, mapping every point
    // to NaN instead of the axis floor.
    vi.stubGlobal('ResizeObserver', ResizeObserverStub)
    renderChart({
      series: [{ key: null, points: [point(1, '2026-08-31T09:00:00Z', 0), point(2, '2026-08-31T09:01:00Z', 0)] }],
      renderMode: 'scatter',
    })

    const circles = screen.getByRole('figure').querySelectorAll('circle')
    expect(circles.length).toBeGreaterThan(0)
    for (const circle of circles) {
      expect(circle.getAttribute('cy')).not.toBe('NaN')
    }
  })

  it('scatter mode draws points only, never a line or area', () => {
    vi.stubGlobal('ResizeObserver', ResizeObserverStub)
    renderChart({
      renderMode: 'scatter',
      series: [{
        key: null,
        points: [point(1, '2026-08-31T09:00:00Z', 10), point(2, '2026-08-31T09:01:00Z', 30), point(3, '2026-08-31T09:02:00Z', 5)],
      }],
    })

    const figure = screen.getByRole('figure')
    expect(figure.querySelectorAll('path')).toHaveLength(0)
    const circles = Array.from(figure.querySelectorAll('svg circle')).filter(
      (c) => c.getAttribute('fill') === CHART_RAMP[0],
    )
    expect(circles).toHaveLength(3)
  })

  it('legend click isolates the clicked series; clicking it again restores all', () => {
    vi.stubGlobal('ResizeObserver', ResizeObserverStub)
    renderChart({
      series: [
        { key: 'a', points: [point(1, '2026-08-31T09:00:00Z', 10)] },
        { key: 'b', points: [point(2, '2026-08-31T09:00:00Z', 20)] },
      ],
      colors: [CHART_RAMP[0]!, CHART_RAMP[1]!],
    })

    fireEvent.click(screen.getByRole('button', { name: 'b' }))
    expect(screen.getByRole('button', { name: 'a' }).getAttribute('aria-pressed')).toBe('false')
    expect(screen.getByRole('button', { name: 'b' }).getAttribute('aria-pressed')).toBe('true')
    // Only the isolated series' line remains (a lone visible series also draws an area
    // path with stroke="none", so excluding that is what isolates the actual line count).
    const strokedPaths = Array.from(screen.getByRole('figure').querySelectorAll('path')).filter(
      (p) => p.getAttribute('stroke') !== 'none',
    )
    expect(strokedPaths).toHaveLength(1)

    fireEvent.click(screen.getByRole('button', { name: 'b' }))
    expect(screen.getByRole('button', { name: 'a' }).getAttribute('aria-pressed')).toBe('true')
    expect(screen.getByRole('button', { name: 'b' }).getAttribute('aria-pressed')).toBe('true')
  })

  it('legend shift-click hides just that one series, leaving the others alone', () => {
    vi.stubGlobal('ResizeObserver', ResizeObserverStub)
    renderChart({
      series: [
        { key: 'a', points: [point(1, '2026-08-31T09:00:00Z', 10)] },
        { key: 'b', points: [point(2, '2026-08-31T09:00:00Z', 20)] },
        { key: 'c', points: [point(3, '2026-08-31T09:00:00Z', 30)] },
      ],
      colors: [CHART_RAMP[0]!, CHART_RAMP[1]!, CHART_RAMP[2]!],
    })

    fireEvent.click(screen.getByRole('button', { name: 'b' }), { shiftKey: true })
    expect(screen.getByRole('button', { name: 'a' }).getAttribute('aria-pressed')).toBe('true')
    expect(screen.getByRole('button', { name: 'b' }).getAttribute('aria-pressed')).toBe('false')
    expect(screen.getByRole('button', { name: 'c' }).getAttribute('aria-pressed')).toBe('true')
  })

  // Review regression — hidden state used to be keyed by array position, so a refetch that
  // re-ranks series by total value (every real refetch, since ranking is by total metric
  // value) would silently hide whichever series now landed on the old index instead of the
  // one the user actually clicked.
  it('keeps the same series hidden by key across a re-rank, not by its old position', () => {
    vi.stubGlobal('ResizeObserver', ResizeObserverStub)
    const props = {
      series: [
        { key: 'a', points: [point(1, '2026-08-31T09:00:00Z', 10)] },
        { key: 'b', points: [point(2, '2026-08-31T09:00:00Z', 20)] },
      ],
      colors: [CHART_RAMP[0]!, CHART_RAMP[1]!],
      height: 240 as const,
      label: 'Context growth',
      formatValue: (v: number) => String(v),
      formatTime: (iso: string) => iso,
    }
    const { rerender } = render(createElement(LineChart, props))

    fireEvent.click(screen.getByRole('button', { name: 'b' }), { shiftKey: true })
    expect(screen.getByRole('button', { name: 'b' }).getAttribute('aria-pressed')).toBe('false')

    // "b" overtakes "a" in total value and a refetch re-ranks it to index 0.
    rerender(
      createElement(LineChart, {
        ...props,
        series: [
          { key: 'b', points: [point(2, '2026-08-31T09:00:00Z', 20)] },
          { key: 'a', points: [point(1, '2026-08-31T09:00:00Z', 10)] },
        ],
      }),
    )

    expect(screen.getByRole('button', { name: 'a' }).getAttribute('aria-pressed')).toBe('true')
    expect(screen.getByRole('button', { name: 'b' }).getAttribute('aria-pressed')).toBe('false')
  })
})