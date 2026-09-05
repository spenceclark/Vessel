import { createElement, type ReactNode } from 'react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { api } from '@/api/client'
import { EMPTY_FILTERS, type AggregateResponse, type AggregateRow, type AggregateDimensionName } from '@/api/types'
import { ReportsView } from './ReportsView'

/**
 * #26 live-use feedback (round 3) — a dimension with exactly one group is dropped
 * entirely for Tokens/Requests/Avg tok/s (they'd only restate the header stats bar);
 * Duration by tag, Cache efficiency and Warnings by type stay (they surface p50/p95, a
 * cached-% ratio, and a warning-code breakdown — none of which the header carries).
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

function row(overrides: Partial<AggregateRow> = {}): AggregateRow {
  return {
    key: 'qwen2.5:7b',
    requests: 78,
    failed: 0,
    tokensIn: 200_800,
    tokensOut: 2648,
    tokensCachedRead: 0,
    tokensCachedWrite: 0,
    avgDurationMs: 400,
    avgTtftMs: null,
    avgTokPerSec: 80.1,
    tokensEstimated: false,
    p50DurationMs: 361,
    p95DurationMs: 1440,
    meanScore: null,
    scored: 0,
    wins: null,
    groups: null,
    ...overrides,
  }
}

function renderView() {
  vi.spyOn(api, 'getSeries').mockResolvedValue({
    metric: 'tokens_in', groupBy: 'none', series: [{ key: null, points: [] }],
    returned: 0, totalMatching: 0, truncated: false, omittedSeries: 0, estimated: false,
  })
  vi.spyOn(api, 'getAggregate').mockImplementation(({ by }: { by: AggregateDimensionName }): Promise<AggregateResponse> => {
    if (by === 'warning') {
      return Promise.resolve({ by: 'warning', rows: [row({ key: null, requests: 78 })], totalGroups: 1 })
    }
    // model and tag both collapse to the session's single (untagged, single-model) group.
    return Promise.resolve({ by, rows: [row({ key: by === 'tag' ? null : 'qwen2.5:7b' })], totalGroups: 1 })
  })

  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: Infinity } } })
  const wrapper = ({ children }: { children: ReactNode }) =>
    createElement(QueryClientProvider, { client: queryClient }, children)
  return render(
    createElement(ReportsView, {
      scope: 20,
      filters: EMPTY_FILTERS,
      onFiltersChange: () => {},
      sessionLabel: 'Session #20',
      enabled: true,
      onSelectRequest: vi.fn(),
    }),
    { wrapper },
  )
}

describe('ReportsView (#26 live-use feedback, round 3)', () => {
  it('drops Tokens/Requests/Avg tok/s by model|tag when degenerate, keeps the rest', async () => {
    vi.stubGlobal('ResizeObserver', ResizeObserverStub)
    renderView()

    // Duration by tag survives — it carries p50/p95, which the header never shows.
    await screen.findByText('Duration by tag')
    // Warnings by type survives — the code breakdown isn't on the header either.
    expect(screen.getByText('Warnings by type')).toBeTruthy()

    // The five cards that would only restate the header stats bar are gone entirely —
    // each fetch is a separate promise, so wait rather than assume they've all settled
    // just because the (independent) Duration/Warnings ones have.
    await waitFor(() => {
      expect(screen.queryByText('Tokens by model')).toBeNull()
      expect(screen.queryByText('Tokens by tag')).toBeNull()
      expect(screen.queryByText('Requests by model')).toBeNull()
      expect(screen.queryByText('Requests by tag')).toBeNull()
      expect(screen.queryByText('Avg tok/s by model')).toBeNull()
    })
  })
})
