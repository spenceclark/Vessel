import { createElement, type ReactNode } from 'react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { api } from '@/api/client'
import { EMPTY_FILTERS, type SeriesResponse } from '@/api/types'
import { ContextGrowthCard } from './ContextGrowthCard'

/**
 * Phase 7 D1/D12 — the card must disclose everything the server did on its behalf:
 * truncation states both numbers, tag grouping discloses the fan-out, dropped series are
 * counted (never merged), estimated counts flag the whole chart approximate. jsdom has no
 * ResizeObserver; the chart stubs its own in the LineChart tests — here the chart renders
 * through the same stub.
 */

afterEach(() => {
  cleanup()
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
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

function seriesResponse(overrides: Partial<SeriesResponse> = {}): SeriesResponse {
  return {
    metric: 'tokens_in',
    groupBy: 'none',
    series: [{ key: null, points: [{ id: 7, t: '2026-08-31T09:00:00Z', v: 18422 }] }],
    returned: 1,
    totalMatching: 0,
    truncated: false,
    omittedSeries: 0,
    estimated: false,
    ...overrides,
  }
}

function renderCard(response: SeriesResponse, groupBy = 'none', hasTags: boolean | undefined = false) {
  const onSelectRequest = vi.fn()
  vi.spyOn(api, 'getSeries').mockResolvedValue(response)
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: Infinity } },
  })
  const wrapper = ({ children }: { children: ReactNode }) =>
    createElement(QueryClientProvider, { client: queryClient }, children)
  const result = render(
    createElement(ContextGrowthCard, {
      scope: 42,
      filters: EMPTY_FILTERS,
      enabled: true,
      hasTags,
      onSelectRequest,
    }),
    { wrapper },
  )
  if (groupBy !== 'none') {
    fireEvent.click(screen.getByRole('tab', { name: groupBy === 'tag' ? 'Tag' : 'Model' }))
  }
  return {
    onSelectRequest,
    rerenderWithHasTags: (next: boolean) =>
      result.rerender(
        createElement(ContextGrowthCard, {
          scope: 42,
          filters: EMPTY_FILTERS,
          enabled: true,
          hasTags: next,
          onSelectRequest,
        }),
      ),
  }
}

describe('ContextGrowthCard (phase 7 #25)', () => {
  it('discloses truncation with both numbers — silent truncation is not acceptable', async () => {
    vi.stubGlobal('ResizeObserver', ResizeObserverStub)
    renderCard(seriesResponse({ truncated: true, returned: 5000, totalMatching: 12043 }))
    expect(await screen.findByText('Most recent 5,000 of 12,043 requests.')).toBeTruthy()
  })

  it('discloses the tag fan-out when grouping by tag', async () => {
    vi.stubGlobal('ResizeObserver', ResizeObserverStub)
    renderCard(seriesResponse({ groupBy: 'tag', series: [{ key: 'planner', points: [] }] }), 'tag')
    expect(await screen.findByText('A request with several tags appears in each.')).toBeTruthy()
  })

  it('discloses ranked-out series instead of silently dropping them', async () => {
    vi.stubGlobal('ResizeObserver', ResizeObserverStub)
    renderCard(seriesResponse({ groupBy: 'tag', omittedSeries: 3, series: [] }), 'tag')
    await screen.findByText(/3 more series not shown/)
  })

  it('flags the whole chart approximate when any drawn row is estimated', async () => {
    vi.stubGlobal('ResizeObserver', ResizeObserverStub)
    renderCard(seriesResponse({ estimated: true }))
    expect(await screen.findByText('~ Estimated token counts — totals are approximate.')).toBeTruthy()
    // The ~ prefix rides the tooltip/table values too (§8.6 formatting through lib/format).
    const table = await screen.findByRole('table', { hidden: true })
    expect(table.textContent).toContain('~18,422')
  })

  it('re-queries with the chosen group-by and shows the empty state when there is no data', async () => {
    vi.stubGlobal('ResizeObserver', ResizeObserverStub)
    const { onSelectRequest } = renderCard(seriesResponse({ series: [], returned: 0 }), 'tag')
    await screen.findByText('No requests with token counts in this scope.')
    expect(api.getSeries).toHaveBeenCalledWith(
      expect.objectContaining({ metric: 'tokens_in', groupBy: 'tag', session: 42 }),
    )
    expect(onSelectRequest).not.toHaveBeenCalled()
  })

  // #25 live-use feedback (round 1) — "None is only a sensible default for untagged
  // traffic": default to Tag once the sibling by-tag fetch proves the scope has any.
  it('defaults to Tag once the scope is known to carry tagged traffic', async () => {
    vi.stubGlobal('ResizeObserver', ResizeObserverStub)
    renderCard(seriesResponse({ groupBy: 'tag', series: [{ key: 'planner', points: [] }] }), 'none', true)
    await screen.findByRole('tab', { name: 'Tag', selected: true })
    expect(api.getSeries).toHaveBeenCalledWith(expect.objectContaining({ groupBy: 'tag' }))
  })

  it('never fights a manually-picked groupBy once hasTags proves true afterward', async () => {
    vi.stubGlobal('ResizeObserver', ResizeObserverStub)
    // hasTags starts undefined (the byTag fetch hasn't resolved yet); the user explicitly
    // picks Model before it does.
    const { rerenderWithHasTags } = renderCard(seriesResponse({ groupBy: 'model' }), 'model', undefined)
    await screen.findByRole('tab', { name: 'Model', selected: true })

    rerenderWithHasTags(true) // the sibling by-tag fetch now resolves true
    await new Promise((r) => setTimeout(r, 0))
    expect(screen.getByRole('tab', { name: 'Model' }).getAttribute('aria-selected')).toBe('true')
    expect(screen.getByRole('tab', { name: 'Tag' }).getAttribute('aria-selected')).toBe('false')
  })

  // #25 live-use feedback (round 1) — the ungrouped view must never connect unrelated
  // interleaved requests with a line; this pins that ContextGrowthCard actually wires
  // LineChart's scatter mode for groupBy=None (LineChart's own tests cover the rendering).
  it('renders groupBy=None as a scatter, never a connected line', async () => {
    vi.stubGlobal('ResizeObserver', ResizeObserverStub)
    renderCard(seriesResponse({
      series: [{ key: null, points: [
        { id: 1, t: '2026-08-31T09:00:00Z', v: 10 },
        { id: 2, t: '2026-08-31T09:01:00Z', v: 6000 },
        { id: 3, t: '2026-08-31T09:02:00Z', v: 20 },
      ] }],
    }))
    // A <figure> exists even before the query resolves (the empty state renders one
    // too), so wait for the real data's own text rather than the figure's mere presence.
    await screen.findByText(/peak 6,000/)
    const figure = screen.getByRole('figure')
    expect(figure.querySelectorAll('path')).toHaveLength(0)
    expect(figure.querySelectorAll('circle').length).toBeGreaterThanOrEqual(3)
  })

  // #25 live-use feedback (round 2) — the Overlay/Grid toggle only makes sense once
  // there's more than one series to separate; a single grouped series (or None, which is
  // never more than one series) has nothing to switch between.
  it('shows the Overlay/Grid toggle only when grouped with more than one series', async () => {
    vi.stubGlobal('ResizeObserver', ResizeObserverStub)
    renderCard(seriesResponse({
      groupBy: 'tag',
      series: [{ key: 'a', points: [{ id: 1, t: '2026-08-31T09:00:00Z', v: 1 }] }, { key: 'b', points: [{ id: 2, t: '2026-08-31T09:00:00Z', v: 2 }] }],
    }), 'tag')
    await screen.findByRole('tab', { name: 'Overlay' })
    expect(screen.getByRole('tab', { name: 'Grid' })).toBeTruthy()
  })

  it('hides the Overlay/Grid toggle for a single-series grouped scope', async () => {
    vi.stubGlobal('ResizeObserver', ResizeObserverStub)
    renderCard(seriesResponse({
      groupBy: 'tag',
      series: [{ key: 'planner', points: [{ id: 1, t: '2026-08-31T09:00:00Z', v: 1 }] }],
    }), 'tag')
    await screen.findByRole('tab', { name: 'Tag', selected: true })
    expect(screen.queryByRole('tab', { name: 'Overlay' })).toBeNull()
  })

  it('Grid mode renders one mini-chart per series instead of the single overlay chart', async () => {
    vi.stubGlobal('ResizeObserver', ResizeObserverStub)
    renderCard(seriesResponse({
      groupBy: 'tag',
      series: [
        { key: 'a', points: [{ id: 1, t: '2026-08-31T09:00:00Z', v: 10 }] },
        { key: 'b', points: [{ id: 2, t: '2026-08-31T09:00:00Z', v: 20 }] },
      ],
    }), 'tag')
    await screen.findByRole('tab', { name: 'Grid' })
    fireEvent.click(screen.getByRole('tab', { name: 'Grid' }))

    // Each mini-chart is its own <figure> — one big overlay chart becomes two small ones.
    expect(await screen.findAllByRole('figure')).toHaveLength(2)
    expect(screen.getByRole('heading', { name: 'a', level: 4 })).toBeTruthy()
    expect(screen.getByRole('heading', { name: 'b', level: 4 })).toBeTruthy()
  })

  // Live-use feedback — switching groupBy while Grid is selected left the stale 'grid'
  // view mode in place even once the new grouping had only one series (or none) to
  // separate: the toggle correctly vanished, but a lone `ContextGrowthSmallMultiples`
  // mini-chart kept rendering instead of falling back to the full-width overlay chart.
  // Same underlying state — a session/filter change collapsing series count would hit it
  // identically, which is why the fix keys the render path off the same condition as the
  // toggle's own visibility, not off `viewMode` alone.
  it('falls back to the overlay chart when Grid is selected but the new grouping has only one series', async () => {
    vi.stubGlobal('ResizeObserver', ResizeObserverStub)
    vi.spyOn(api, 'getSeries').mockImplementation(({ groupBy }: { groupBy?: string } = {}) =>
      Promise.resolve(seriesResponse(
        groupBy === 'model'
          ? { groupBy: 'model', series: [{ key: 'llama3.1', points: [{ id: 1, t: '2026-08-31T09:00:00Z', v: 10 }] }] }
          : {
              groupBy: 'tag',
              series: [
                { key: 'a', points: [{ id: 1, t: '2026-08-31T09:00:00Z', v: 10 }] },
                { key: 'b', points: [{ id: 2, t: '2026-08-31T09:00:00Z', v: 20 }] },
              ],
            },
      )),
    )
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: Infinity } } })
    const wrapper = ({ children }: { children: ReactNode }) =>
      createElement(QueryClientProvider, { client: queryClient }, children)
    render(
      createElement(ContextGrowthCard, {
        scope: 42, filters: EMPTY_FILTERS, enabled: true, hasTags: false, onSelectRequest: vi.fn(),
      }),
      { wrapper },
    )

    fireEvent.click(screen.getByRole('tab', { name: 'Tag' }))
    await screen.findByRole('tab', { name: 'Grid' })
    fireEvent.click(screen.getByRole('tab', { name: 'Grid' }))
    expect(await screen.findAllByRole('figure')).toHaveLength(2) // small multiples, confirmed active

    fireEvent.click(screen.getByRole('tab', { name: 'Model' }))
    await waitFor(() => expect(screen.queryAllByRole('figure')).toHaveLength(1))
    expect(screen.queryByRole('tab', { name: 'Grid' })).toBeNull()
    expect(screen.queryByRole('heading', { level: 4 })).toBeNull() // no mini-chart titles left
  })
})