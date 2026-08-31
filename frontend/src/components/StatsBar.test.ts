import { createElement, type ReactNode } from 'react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { api } from '@/api/client'
import type { ConfigGetResponse, SessionInfo, StatsResponse, StatusPayload } from '@/api/types'
import { StatsBar } from './StatsBar'

/**
 * Issue #11 — the default backend stays Ollama, so a first run on a machine without it has
 * a dead default. When that run's one-shot probe found nothing listening, settings open on
 * Config, whose first control is the known-backend picker (#9), so a cloud-only user can
 * configure OpenAI/Claude before ever seeing a 502. Every other case stays silent.
 */

afterEach(cleanup)

const STATS: StatsResponse = {
  total: 0,
  failed: 0,
  avgDurationMs: null,
  avgTokPerSec: null,
  avgTtftMs: null,
  sessionId: 1,
  sessionStartedAt: '2026-08-30T00:00:00.0000000Z',
  tokensIn: 0,
  tokensOut: 0,
  tokensCachedRead: 0,
  tokensCachedWrite: 0,
  tokensEstimated: false,
}

const CONFIG: ConfigGetResponse = {
  config: {
    listen: '127.0.0.1:4550',
    defaultBackend: 'ollama',
    backends: { ollama: { baseUrl: 'http://localhost:11434', type: 'ollama' } },
    timeouts: { activitySeconds: 1800 },
    retention: { maxRequests: 10_000, maxDbSizeMb: 500 },
    capture: { maxBodyMb: 32 },
    warnings: { slowTtftMs: 5000 },
    mcp: { enabled: true },
  },
  restartRequired: [],
}

function status(setup: StatusPayload['setup'], health: 'green' | 'red' | 'unknown' = 'unknown'): StatusPayload {
  return {
    name: 'vessel',
    version: '0.1.0',
    listen: 'http://127.0.0.1:4550',
    defaultBackend: 'ollama',
    backends: [
      {
        name: 'ollama',
        baseUrl: 'http://localhost:11434',
        type: 'ollama',
        default: true,
        health: { state: health, lastSeenAt: null },
      },
    ],
    capture: { recording: true },
    mcp: { enabled: true },
    listenSecurity: { isNonLoopback: false, isContainer: false },
    serverRunId: 'run-1',
    setup,
  }
}

const SESSIONS: SessionInfo[] = [
  {
    id: 3, startedAt: '2026-08-30T02:00:00Z', name: 'run-42', isCurrent: false,
    requestCount: 8, lastRequestAt: '2026-08-30T02:30:00Z',
  },
  {
    id: 1, startedAt: STATS.sessionStartedAt!, name: 'session 1', isCurrent: true,
    requestCount: 1, lastRequestAt: STATS.sessionStartedAt,
  },
]

function renderStatsBar(
  payload: StatusPayload,
  onScopeChange: (scope: number | 'all') => void = () => {},
  sessions: SessionInfo[] = SESSIONS,
  onDataCleared?: (scope: { all: true } | { before: string }) => void,
  onDeleteSessions: (sessionIds: number[]) => Promise<{ sessionsDeleted: number; requestsDeleted: number }>
    = async (sessionIds) => ({ sessionsDeleted: sessionIds.length, requestsDeleted: 0 }),
) {
  vi.spyOn(api, 'getStats').mockResolvedValue(STATS)
  vi.spyOn(api, 'getStatus').mockResolvedValue(payload)
  vi.spyOn(api, 'getConfig').mockResolvedValue(CONFIG)

  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: Infinity, staleTime: Infinity } },
  })
  const wrapper = ({ children }: { children: ReactNode }) =>
    createElement(QueryClientProvider, { client: queryClient }, children)
  render(
    createElement(StatsBar, {
      scope: 1,
      sessions,
      onScopeChange,
      onReset: () => Promise.resolve(),
      onDataCleared,
      onDeleteSessions,
      connected: true,
    }),
    { wrapper },
  )
  return queryClient
}

describe('StatsBar session picker (issue #29)', () => {
  it('shows named sessions newest-first and changes the selected scope', async () => {
    const onScopeChange = vi.fn()
    renderStatsBar(status({ firstRun: false, defaultBackendReachable: null }), onScopeChange)

    const picker = screen.getByRole('button', { name: 'Session' })
    fireEvent.click(picker)
    expect(screen.getAllByRole('option').map((option) => option.textContent)).toEqual([
      'All sessionsFull captured history',
      expect.stringMatching(/^session 1 · #1 · current1 request · /),
      expect.stringMatching(/^run-42 · #38 requests · /),
    ])

    fireEvent.click(screen.getByRole('option', { name: /run-42/ }))
    expect(onScopeChange).toHaveBeenCalledWith(3)
    fireEvent.click(picker)
    fireEvent.click(screen.getByRole('option', { name: /All sessions/ }))
    expect(onScopeChange).toHaveBeenCalledWith('all')
  })

  it('limits the recent list to 15 while type-ahead finds older sessions', () => {
    const sessions = Array.from({ length: 17 }, (_, index): SessionInfo => ({
      id: 18 - index,
      startedAt: `2026-08-${String(29 - index).padStart(2, '0')}T00:00:00Z`,
      name: `run-${18 - index}`,
      isCurrent: false,
      requestCount: index,
      lastRequestAt: null,
    }))
    sessions.push({ ...SESSIONS[1] })
    renderStatsBar(status({ firstRun: false, defaultBackendReachable: null }), () => {}, sessions)

    fireEvent.click(screen.getByRole('button', { name: 'Session' }))
    expect(screen.getAllByRole('option')).toHaveLength(17) // All + current + 15 recent
    expect(screen.queryByRole('option', { name: /run-2 ·/ })).toBeNull()

    fireEvent.change(screen.getByRole('textbox', { name: 'Filter sessions' }), { target: { value: 'run-2' } })
    expect(screen.getByRole('option', { name: /run-2 ·/ })).toBeTruthy()
  })
})

describe('Session deletion UX (issue #41 feedback)', () => {
  it('confirms one non-current picker row by visible request count without typing', async () => {
    const onDeleteSessions = vi.fn(async () => ({ sessionsDeleted: 1, requestsDeleted: 8 }))
    renderStatsBar(
      status({ firstRun: false, defaultBackendReachable: null }),
      () => {},
      SESSIONS,
      undefined,
      onDeleteSessions,
    )

    fireEvent.click(screen.getByRole('button', { name: 'Session' }))
    expect(screen.queryByRole('button', { name: /Delete session 1/ })).toBeNull()
    fireEvent.click(screen.getByRole('button', { name: /Delete run-42/ }))

    const confirmation = screen.getByRole('alertdialog', { name: 'Confirm session deletion' })
    expect(confirmation.textContent).toContain('Delete run-42 — 8 requests?')
    expect(screen.queryByPlaceholderText('DELETE')).toBeNull()
    fireEvent.click(screen.getByRole('button', { name: 'Delete' }))

    await waitFor(() => expect(onDeleteSessions).toHaveBeenCalledWith([3]))
  })

  it('uses a typed-confirmation checklist for bulk deletion and disables current', async () => {
    const sessions = [
      { ...SESSIONS[0] },
      {
        id: 4, startedAt: '2026-08-30T03:00:00Z', name: 'run-43', isCurrent: false,
        requestCount: 2, lastRequestAt: '2026-08-30T03:10:00Z',
      },
      { ...SESSIONS[1] },
    ]
    const onDeleteSessions = vi.fn(async () => ({ sessionsDeleted: 2, requestsDeleted: 10 }))
    renderStatsBar(
      status({ firstRun: false, defaultBackendReachable: null }),
      () => {},
      sessions,
      undefined,
      onDeleteSessions,
    )

    fireEvent.click(screen.getByRole('button', { name: 'Settings' }))
    await screen.findByRole('dialog')
    fireEvent.click(screen.getByRole('button', { name: 'Delete sessions…' }))

    const checkboxes = screen.getAllByRole('checkbox') as HTMLInputElement[]
    expect(checkboxes).toHaveLength(3)
    expect(checkboxes[2].disabled).toBe(true)
    fireEvent.click(checkboxes[0])
    fireEvent.click(checkboxes[1])
    const confirm = screen.getByRole('button', { name: 'Confirm delete' })
    expect((confirm as HTMLButtonElement).disabled).toBe(true)
    fireEvent.change(screen.getByPlaceholderText('DELETE'), { target: { value: 'DELETE' } })
    fireEvent.click(confirm)

    await waitFor(() => expect(onDeleteSessions).toHaveBeenCalledWith([3, 4]))
    expect(await screen.findByText('Deleted 2 sessions and 10 requests.')).toBeTruthy()
  })
})

describe('StatsBar first-run backend setup (issue #11)', () => {
  it('opens settings on the backend picker when the first-run probe found nothing listening', async () => {
    renderStatsBar(status({ firstRun: true, defaultBackendReachable: false }))

    expect(await screen.findByRole('dialog')).toBeTruthy()
    // The Config tab, not the Data tab settings otherwise opens on.
    expect(await screen.findByLabelText('Add backend')).toBeTruthy()
  })

  it('stays silent on a first run whose default backend answered', async () => {
    renderStatsBar(status({ firstRun: true, defaultBackendReachable: true }, 'green'))

    // The backend indicator renders only once `['status']` has resolved — waiting on
    // anything rendered earlier would assert "no dialog" before the effect could open one.
    await screen.findByText('ollama')
    expect(screen.queryByRole('dialog')).toBeNull()
  })

  // PR review — same staleness on this surface: reloading the UI after the user started
  // Ollama must not reopen the picker for a backend that is now answering.
  it('stays silent once a captured request has superseded the probe with green health', async () => {
    renderStatsBar(status({ firstRun: true, defaultBackendReachable: false }, 'green'))

    // The backend indicator renders only once `['status']` has resolved — waiting on
    // anything rendered earlier would assert "no dialog" before the effect could open one.
    await screen.findByText('ollama')
    expect(screen.queryByRole('dialog')).toBeNull()
  })

  it('stays silent on later runs, which never probe — red health alone is the nudge’s job', async () => {
    renderStatsBar(status({ firstRun: false, defaultBackendReachable: null }, 'red'))

    // The backend indicator renders only once `['status']` has resolved — waiting on
    // anything rendered earlier would assert "no dialog" before the effect could open one.
    await screen.findByText('ollama')
    expect(screen.queryByRole('dialog')).toBeNull()
  })

  it('does not reopen after the user dismisses it, however often status refetches', async () => {
    const queryClient = renderStatsBar(status({ firstRun: true, defaultBackendReachable: false }))

    await screen.findByRole('dialog')
    fireEvent.click(screen.getByLabelText('Close'))
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())

    await queryClient.invalidateQueries({ queryKey: ['status'] })

    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
  })
})
