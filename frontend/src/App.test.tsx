import { createElement, useState, type ReactNode } from 'react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { api, ApiError } from '@/api/client'
import { requestDetailQueryKey } from '@/api/queryKeys'
import { EMPTY_FILTERS, SESSION_LIST_LIMIT, type ClearedEvent, type RequestDetail, type RequestFilters, type SessionDeleteSummary, type SessionInfo, type SessionScope } from '@/api/types'
import App from './App'

interface MockStatsProps {
  scope: SessionScope | null
  onScopeChange: (scope: SessionScope) => void
  onReset: () => Promise<void>
  onDeleteSessions: (sessionIds: number[]) => Promise<SessionDeleteSummary>
}

interface MockFilterBarProps {
  filters: RequestFilters
  onFiltersChange: (next: RequestFilters) => void
}

// #105 — the hook's clear callback is App's only selection reconciliation, so tests drive it.
const live = vi.hoisted(() => ({ onCleared: undefined as ((event: ClearedEvent) => void) | undefined }))
vi.mock('@/api/useLiveHistory', () => ({
  useLiveHistory: (options: { onCleared?: (event: ClearedEvent) => void }) => {
    live.onCleared = options.onCleared
    return { inFlight: [], connected: true, newSinceFilter: 0, clearNewSinceFilter: () => {} }
  },
}))
vi.mock('@/components/StatsBar', () => ({
  StatsBar: ({ scope, onScopeChange, onReset, onDeleteSessions }: MockStatsProps) => {
    const [result, setResult] = useState<SessionDeleteSummary | null>(null)
    return (
      <>
        <div data-testid="scope">{String(scope)}</div>
        <button type="button" onClick={() => onScopeChange(3)}>Scope test</button>
        <button type="button" onClick={() => void onReset()}>Reset test</button>
        <button type="button" onClick={() => void onDeleteSessions([2, 3]).then(setResult)}>Bulk delete test</button>
        {result && <div data-testid="delete-result">{JSON.stringify(result)}</div>}
      </>
    )
  },
}))
vi.mock('@/components/CaptureHealthBanner', () => ({ CaptureHealthBanner: () => null }))
vi.mock('@/components/BindAddressBanner', () => ({ BindAddressBanner: () => null }))
vi.mock('@/components/FilterBar', () => ({
  FilterBar: ({ filters, onFiltersChange }: MockFilterBarProps) => (
    <>
      <div data-testid="filters">{JSON.stringify(filters)}</div>
      <button type="button" onClick={() => onFiltersChange({ ...filters, tag: 'planner' })}>Filter test</button>
    </>
  ),
}))
vi.mock('@/components/RequestList', () => ({
  RequestList: ({ onSelectRow }: { onSelectRow: (id: number) => void }) => (
    <button type="button" onClick={() => onSelectRow(5)}>Select row 5</button>
  ),
}))
vi.mock('@/components/DetailPane', () => ({
  DetailPane: ({ id }: { id: number | null }) => <div data-testid="selected">{String(id)}</div>,
}))
vi.mock('@/components/InFlightDetailPane', () => ({ InFlightDetailPane: () => null }))
vi.mock('@/components/CompareView', () => ({ CompareView: () => null }))

afterEach(() => {
  cleanup()
  vi.restoreAllMocks()
})

const current: SessionInfo = {
  id: 1,
  startedAt: '2026-08-31T00:00:00Z',
  name: 'session 1',
  isCurrent: true,
  requestCount: 1,
  lastRequestAt: '2026-08-31T00:00:01Z',
}

function renderApp() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: Infinity, staleTime: Infinity } },
  })
  const wrapper = ({ children }: { children: ReactNode }) =>
    createElement(QueryClientProvider, { client: queryClient }, children)
  render(createElement(App), { wrapper })
  return queryClient
}

describe('App session coordination review regressions', () => {
  it('does not let the stale pre-refetch session list bounce Reset back to old current', async () => {
    let resolveRefetch: ((sessions: SessionInfo[]) => void) | undefined
    vi.spyOn(api, 'listSessions')
      .mockResolvedValueOnce([current])
      .mockImplementationOnce(() => new Promise((resolve) => { resolveRefetch = resolve }))
    const next = { ...current, id: 2, name: 'session 2', isCurrent: true, requestCount: 0, lastRequestAt: null }
    vi.spyOn(api, 'createSession').mockResolvedValue(next)

    renderApp()
    await waitFor(() => expect(screen.getByTestId('scope').textContent).toBe('1'))
    fireEvent.click(screen.getByRole('button', { name: 'Reset test' }))

    await waitFor(() => expect(resolveRefetch).toBeDefined())
    expect(screen.getByTestId('scope').textContent).toBe('2')

    resolveRefetch!([next, { ...current, isCurrent: false }])
    await waitFor(() => expect(screen.getByTestId('scope').textContent).toBe('2'))
  })

  it('keeps the viewed session when a full page means the bounded list may be truncated', async () => {
    // GET /sessions returns current plus at most SESSION_LIST_LIMIT - 1 other markers, so a
    // session outside that window is absent from the listing without having been deleted.
    vi.spyOn(api, 'listSessions').mockResolvedValue([
      ...Array.from({ length: SESSION_LIST_LIMIT - 1 }, (_, index): SessionInfo => ({
        ...current, id: 1000 + index, name: `run-${1000 + index}`, isCurrent: false,
      })),
      current,
    ])

    renderApp()
    await waitFor(() => expect(screen.getByTestId('scope').textContent).toBe('1'))
    fireEvent.click(screen.getByRole('button', { name: 'Scope test' }))

    await waitFor(() => expect(screen.getByTestId('scope').textContent).toBe('3'))
    expect(screen.getByTestId('scope').textContent).toBe('3')
  })

  it('returns to current when a short list proves the viewed session was deleted', async () => {
    vi.spyOn(api, 'listSessions').mockResolvedValue([current])

    renderApp()
    await waitFor(() => expect(screen.getByTestId('scope').textContent).toBe('1'))
    fireEvent.click(screen.getByRole('button', { name: 'Scope test' }))

    await waitFor(() => expect(screen.getByTestId('scope').textContent).toBe('1'))
  })

  // Live-use feedback — a filter set for one session's traffic rarely still applies to
  // another's; carrying it across an explicit session change read as a stale leftover.
  // Uses the same bounded session list as "keeps the viewed session..." below, so scope
  // 3 actually sticks (not bounced back to current) and the scope change is unambiguous.
  it('resets filters when the session scope changes', async () => {
    vi.spyOn(api, 'listSessions').mockResolvedValue([
      ...Array.from({ length: SESSION_LIST_LIMIT - 1 }, (_, index): SessionInfo => ({
        ...current, id: 1000 + index, name: `run-${1000 + index}`, isCurrent: false,
      })),
      current,
    ])

    renderApp()
    await waitFor(() => expect(screen.getByTestId('scope').textContent).toBe('1'))

    fireEvent.click(screen.getByRole('button', { name: 'Filter test' }))
    expect(JSON.parse(screen.getByTestId('filters').textContent!)).toEqual({ ...EMPTY_FILTERS, tag: 'planner' })

    fireEvent.click(screen.getByRole('button', { name: 'Scope test' })) // -> scope 3
    await waitFor(() => expect(screen.getByTestId('scope').textContent).toBe('3'))
    expect(JSON.parse(screen.getByTestId('filters').textContent!)).toEqual(EMPTY_FILTERS)
  })

  it('attempts every bulk deletion and reports prior successes with failures', async () => {
    vi.spyOn(api, 'listSessions').mockResolvedValue([
      { ...current, id: 3, name: 'run-3', isCurrent: false },
      { ...current, id: 2, name: 'run-2', isCurrent: false },
      current,
    ])
    const deletion = vi.spyOn(api, 'deleteSession')
      .mockResolvedValueOnce({ deleted: 4 })
      .mockRejectedValueOnce(new ApiError(409, 'invalid_request', 'session is in use'))

    renderApp()
    await waitFor(() => expect(screen.getByTestId('scope').textContent).toBe('1'))
    fireEvent.click(screen.getByRole('button', { name: 'Bulk delete test' }))

    await waitFor(() => expect(deletion).toHaveBeenCalledTimes(2))
    expect(deletion.mock.calls.map(([id]) => id)).toEqual([2, 3])
    await waitFor(() => expect(JSON.parse(screen.getByTestId('delete-result').textContent!)).toEqual({
      sessionsDeleted: 1,
      requestsDeleted: 4,
      failures: [{ sessionId: 3, message: 'session is in use' }],
    }))
  })
})

// #105 — every clear reaches every tab as the `cleared` frame, the tab that made it included, so
// the selection is reconciled there and nowhere else. A before clear's cut-off and a session
// clear's id decide whether the selected capture was reached; an all clear always reaches it.
describe('App selection on a received clear', () => {
  async function selectRow(detail: Partial<RequestDetail> | null) {
    vi.spyOn(api, 'listSessions').mockResolvedValue([current])
    const queryClient = renderApp()
    await waitFor(() => expect(screen.getByTestId('scope').textContent).toBe('1'))
    if (detail) queryClient.setQueryData(requestDetailQueryKey(5), { id: 5, ...detail })
    fireEvent.click(screen.getByRole('button', { name: 'Select row 5' }))
    expect(screen.getByTestId('selected').textContent).toBe('5')
  }

  function clear(event: ClearedEvent) {
    act(() => live.onCleared!(event))
  }

  it('deselects on an all clear', async () => {
    await selectRow({ startedAt: '2026-09-01T00:00:00Z', sessionId: 1 })
    clear({})
    expect(screen.getByTestId('selected').textContent).toBe('null')
  })

  it('keeps a before clear survivor and deselects a row the cut-off reached', async () => {
    await selectRow({ startedAt: '2026-09-01T00:00:00Z', sessionId: 1 })
    clear({ before: '2026-09-01T00:00:00.0000000Z' })
    expect(screen.getByTestId('selected').textContent).toBe('5')
    clear({ before: '2026-09-01T00:00:00.0010000Z' })
    expect(screen.getByTestId('selected').textContent).toBe('null')
  })

  it('deselects only for a session clear of the selected row\'s session', async () => {
    await selectRow({ startedAt: '2026-09-01T00:00:00Z', sessionId: 1 })
    clear({ sessionId: 2 })
    expect(screen.getByTestId('selected').textContent).toBe('5')
    clear({ sessionId: 1 })
    expect(screen.getByTestId('selected').textContent).toBe('null')
  })

  it('assumes an uncached selection was reached', async () => {
    await selectRow(null)
    clear({ sessionId: 2 })
    expect(screen.getByTestId('selected').textContent).toBe('null')
  })
})
