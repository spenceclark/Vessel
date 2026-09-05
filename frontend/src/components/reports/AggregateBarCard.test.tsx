import { cleanup, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { AggregateResponse, AggregateRow } from '@/api/types'
import { AggregateBarCard } from './AggregateBarCard'

/**
 * #26 live-use feedback — a single-member grouping collapses to a `StatPanel` (tiles, not
 * plain prose — round 3 feedback: "can it look nicer than plain text") instead of a
 * one-bar chart (no comparison to draw); the cap note always says "by tokens" (the actual
 * server ordering, D2), regardless of which measure the card itself plots; the fan-out
 * note is worded per dimension (tag vs warning).
 */

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
})

class ResizeObserverStub {
  private readonly callback: ResizeObserverCallback
  constructor(callback: ResizeObserverCallback) {
    this.callback = callback
  }
  observe(): void {
    this.callback(
      [{ contentRect: { width: 400, height: 180, x: 0, y: 0, top: 0, left: 0, bottom: 180, right: 400 } } as ResizeObserverEntry],
      this as unknown as ResizeObserver,
    )
  }
  unobserve(): void {}
  disconnect(): void {}
}

function row(overrides: Partial<AggregateRow> = {}): AggregateRow {
  return {
    key: 'llama3.1',
    requests: 10,
    failed: 0,
    tokensIn: 1000,
    tokensOut: 100,
    tokensCachedRead: 0,
    tokensCachedWrite: 0,
    avgDurationMs: 200,
    avgTtftMs: null,
    avgTokPerSec: 40,
    tokensEstimated: false,
    p50DurationMs: 180,
    p95DurationMs: 400,
    meanScore: null,
    scored: 0,
    wins: null,
    groups: null,
    ...overrides,
  }
}

function response(rows: AggregateRow[], totalGroups = rows.length): AggregateResponse {
  return { by: 'model', rows, totalGroups }
}

describe('AggregateBarCard (#26 live-use feedback)', () => {
  it('collapses a single-group scope to a stat panel instead of a one-bar chart', () => {
    vi.stubGlobal('ResizeObserver', ResizeObserverStub)
    render(
      <AggregateBarCard
        title="Requests by model"
        data={response([row({ key: 'llama3.1', requests: 312, failed: 1 })], 1)}
        by="model"
        projection="requests"
        loading={false}
      />,
    )
    expect(screen.getByText('llama3.1')).toBeTruthy() // the group's own name, once
    expect(screen.getByText('Requests')).toBeTruthy()
    expect(screen.getByText('312')).toBeTruthy()
    const failedValue = screen.getByText('1')
    expect(failedValue.className).toContain('text-danger')
    expect(document.querySelector('svg')).toBeNull() // no chart drawn for a degenerate scope
  })

  it('omits the failed tile entirely when nothing failed', () => {
    vi.stubGlobal('ResizeObserver', ResizeObserverStub)
    render(
      <AggregateBarCard
        title="Requests by model"
        data={response([row({ key: 'llama3.1', requests: 312, failed: 0 })], 1)}
        by="model"
        projection="requests"
        loading={false}
      />,
    )
    expect(screen.queryByText('Failed')).toBeNull()
  })

  it('the duration stat panel shows — for a group with no measured duration', () => {
    vi.stubGlobal('ResizeObserver', ResizeObserverStub)
    render(
      <AggregateBarCard
        title="Duration by tag"
        data={response([row({ key: null, p50DurationMs: null, p95DurationMs: null })], 1)}
        by="tag"
        projection="duration"
        loading={false}
      />,
    )
    expect(screen.getByText('(none)')).toBeTruthy()
    expect(screen.getByText('p50')).toBeTruthy()
    expect(screen.getAllByText('—')).toHaveLength(2) // p50 and p95 tiles
  })

  it('renders the cache-efficiency stacked bar for a multi-group scope', () => {
    vi.stubGlobal('ResizeObserver', ResizeObserverStub)
    render(
      <AggregateBarCard
        title="Cache efficiency"
        data={response([
          row({ key: 'm1', tokensIn: 1000, tokensCachedRead: 400 }),
          row({ key: 'm2', tokensIn: 2000, tokensCachedRead: 0 }),
        ])}
        by="model"
        projection="cache"
        loading={false}
      />,
    )
    const legend = screen.getByRole('group', { name: 'Series' })
    expect(legend.textContent).toContain('cached')
    expect(legend.textContent).toContain('uncached')
    const table = screen.getByRole('table', { hidden: true })
    // m1: 400 cached / 600 uncached; m2: 0 cached / 2000 uncached.
    expect(table.textContent).toContain('m1')
    expect(table.textContent).toContain('m2')
  })

  it('words the fan-out note per dimension — tag vs warning', () => {
    vi.stubGlobal('ResizeObserver', ResizeObserverStub)
    const { rerender } = render(
      <AggregateBarCard
        title="Tokens by tag"
        data={response([row({ key: 'a' }), row({ key: 'b', tokensIn: 5 })])}
        by="tag"
        projection="tokens"
        loading={false}
      />,
    )
    expect(screen.getByText('A request with several tags is counted once per tag.')).toBeTruthy()

    rerender(
      <AggregateBarCard
        title="Warnings by type"
        data={response([row({ key: 'cold_load' }), row({ key: 'slow_ttft', tokensIn: 5 })])}
        by="warning"
        projection="requests"
        loading={false}
      />,
    )
    expect(screen.getByText('A request with several warnings is counted once per warning.')).toBeTruthy()
  })

  it('caps the display to 8 rows and discloses "by tokens" regardless of projection', () => {
    vi.stubGlobal('ResizeObserver', ResizeObserverStub)
    const rows = Array.from({ length: 12 }, (_, i) => row({ key: `m${i}`, tokensIn: 100 - i }))
    render(
      <AggregateBarCard title="Requests by model" data={response(rows, 12)} by="model" projection="requests" loading={false} />,
    )
    expect(screen.getByText('Top 8 of 12 by tokens.')).toBeTruthy()
  })
})
