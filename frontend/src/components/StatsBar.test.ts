import { createElement, type ReactNode } from 'react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { api } from '@/api/client'
import type { ConfigGetResponse, StatsResponse, StatusPayload } from '@/api/types'
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

function renderStatsBar(payload: StatusPayload) {
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
      currentSessionId: 1,
      onScopeChange: () => {},
      onReset: () => Promise.resolve(),
      connected: true,
    }),
    { wrapper },
  )
  return queryClient
}

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
