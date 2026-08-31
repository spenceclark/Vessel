import { createElement, useState, type ReactNode } from 'react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { api, ApiError } from '@/api/client'
import { SESSION_LIST_LIMIT, type SessionDeleteSummary, type SessionInfo, type SessionScope } from '@/api/types'
import App from './App'

interface MockStatsProps {
  scope: SessionScope | null
  onScopeChange: (scope: SessionScope) => void
  onReset: () => Promise<void>
  onDeleteSessions: (sessionIds: number[]) => Promise<SessionDeleteSummary>
}

vi.mock('@/api/useLiveHistory', () => ({
  useLiveHistory: () => ({
    inFlight: [], connected: true, newSinceFilter: 0, clearNewSinceFilter: () => {},
  }),
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
vi.mock('@/components/FilterBar', () => ({ FilterBar: () => null }))
vi.mock('@/components/RequestList', () => ({ RequestList: () => null }))
vi.mock('@/components/DetailPane', () => ({ DetailPane: () => null }))
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
