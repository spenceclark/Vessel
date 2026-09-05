import { createElement, type ReactNode } from 'react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, fireEvent, waitFor, cleanup } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { api } from '@/api/client'
import { EMPTY_FILTERS, type RequestListResponse, type StatusPayload, type Summary } from '@/api/types'
import type { Selection } from '@/App'
import { RequestList } from './RequestList'

/**
 * Issue #6 — ↑/↓ should move the selection to the prev/next request (email-client
 * pattern) rather than scrolling the pane, and must not hijack the search box while a
 * text input is focused.
 *
 * Issue #11 — with no rows to show, the empty state is the only place a dead default
 * backend can be named, so it replaces "No requests yet" with the nudge whenever the
 * default backend is known to be unreachable (passive red health, or the first-run probe).
 */

afterEach(cleanup)

// jsdom has no layout engine — the virtualizer's scroll container measures 0x0 by
// default, so it renders zero virtual items regardless of row count. Stub a real size
// (and a no-op ResizeObserver, which jsdom doesn't implement) so rows actually render.
class MockResizeObserver {
  observe() {}
  unobserve() {}
  disconnect() {}
}
vi.stubGlobal('ResizeObserver', MockResizeObserver)
Object.defineProperty(HTMLElement.prototype, 'offsetHeight', { configurable: true, value: 600 })
Object.defineProperty(HTMLElement.prototype, 'offsetWidth', { configurable: true, value: 400 })

function summary(id: number, overrides: Partial<Summary> = {}): Summary {
  return {
    id,
    startedAt: '2026-08-28T00:00:01.0000000Z',
    sessionId: 1,
    backend: 'stub',
    tags: [],
    method: 'POST',
    path: `/api/chat/${id}`,
    format: 'raw',
    model: null,
    statusCode: 200,
    error: null,
    streamed: false,
    replayOf: null,
    replayGroup: null,
    replayPatch: null,
    score: null,
    durationMs: 10,
    ttftMs: null,
    vesselOverheadMs: 1,
    tokPerSec: null,
    tokensIn: null,
    tokensOut: null,
    tokensCachedRead: null,
    tokensCachedWrite: null,
    tokensEstimated: false,
    stopReason: null,
    warnings: [],
    truncated: false,
    ...overrides,
  }
}

const NO_REQUESTS = 'No requests yet — traffic through Vessel will show up here.'

function status(overrides: {
  backendName?: string
  baseUrl?: string
  health?: 'green' | 'red' | 'unknown'
  firstRun?: boolean
  defaultBackendReachable?: boolean | null
} = {}): StatusPayload {
  return {
    name: 'vessel',
    version: '0.1.0',
    listen: 'http://127.0.0.1:4550',
    defaultBackend: overrides.backendName ?? 'ollama',
    backends: [
      {
        name: overrides.backendName ?? 'ollama',
        baseUrl: overrides.baseUrl ?? 'http://localhost:11434',
        type: 'ollama',
        default: true,
        health: { state: overrides.health ?? 'unknown', lastSeenAt: null },
        requiresAuth: false,
      },
    ],
    capture: { recording: true },
    mcp: { enabled: true },
    listenSecurity: { isNonLoopback: false, isContainer: false },
    serverRunId: 'run-1',
    setup: {
      firstRun: overrides.firstRun ?? false,
      defaultBackendReachable: overrides.defaultBackendReachable ?? null,
    },
  }
}

function renderList(
  rows: Summary[],
  selection: Selection | null,
  onSelectRow: (id: number) => void,
  statusPayload: StatusPayload = status(),
) {
  const response: RequestListResponse = { rows, nextBefore: null }
  vi.spyOn(api, 'listRequests').mockResolvedValue(response)
  vi.spyOn(api, 'getStatus').mockResolvedValue(statusPayload)
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: Infinity, staleTime: Infinity } },
  })
  const wrapper = ({ children }: { children: ReactNode }) => createElement(QueryClientProvider, { client: queryClient }, children)
  return render(
    createElement(RequestList, {
      scope: 1,
      filters: EMPTY_FILTERS,
      inFlight: [],
      newSinceFilter: 0,
      onClearNewSinceFilter: () => {},
      selection,
      onSelectRow,
      onSelectInFlight: () => {},
    }),
    { wrapper },
  )
}

describe('RequestList arrow-key navigation (issue #6)', () => {
  it('ArrowDown moves selection to the next request', async () => {
    const rows = [summary(3), summary(2), summary(1)]
    let selection: Selection | null = { kind: 'row', id: 3 }
    const onSelectRow = vi.fn((id: number) => {
      selection = { kind: 'row', id }
    })
    renderList(rows, selection, onSelectRow)

    await waitFor(() => expect(screen.getByText('/api/chat/3')).toBeTruthy())

    fireEvent.keyDown(window, { key: 'ArrowDown' })

    expect(onSelectRow).toHaveBeenCalledWith(2)
  })

  it('ArrowUp moves selection to the previous request', async () => {
    const rows = [summary(3), summary(2), summary(1)]
    const selection: Selection = { kind: 'row', id: 2 }
    const onSelectRow = vi.fn()
    renderList(rows, selection, onSelectRow)

    await waitFor(() => expect(screen.getByText('/api/chat/2')).toBeTruthy())

    fireEvent.keyDown(window, { key: 'ArrowUp' })

    expect(onSelectRow).toHaveBeenCalledWith(3)
  })

  it('does not move past the last item', async () => {
    const rows = [summary(3), summary(2), summary(1)]
    const selection: Selection = { kind: 'row', id: 1 }
    const onSelectRow = vi.fn()
    renderList(rows, selection, onSelectRow)

    await waitFor(() => expect(screen.getByText('/api/chat/1')).toBeTruthy())

    fireEvent.keyDown(window, { key: 'ArrowDown' })

    expect(onSelectRow).not.toHaveBeenCalled()
  })

  it('ignores arrow keys while focus is in a text input', async () => {
    const rows = [summary(3), summary(2), summary(1)]
    const selection: Selection = { kind: 'row', id: 3 }
    const onSelectRow = vi.fn()
    renderList(rows, selection, onSelectRow)

    await waitFor(() => expect(screen.getByText('/api/chat/3')).toBeTruthy())

    const searchInput = document.createElement('input')
    document.body.appendChild(searchInput)
    searchInput.focus()

    fireEvent.keyDown(searchInput, { key: 'ArrowDown' })

    expect(onSelectRow).not.toHaveBeenCalled()
    document.body.removeChild(searchInput)
  })

  it('ignores arrow keys when nothing is selected', async () => {
    const rows = [summary(3), summary(2), summary(1)]
    const onSelectRow = vi.fn()
    renderList(rows, null, onSelectRow)

    await waitFor(() => expect(screen.getByText('/api/chat/3')).toBeTruthy())

    fireEvent.keyDown(window, { key: 'ArrowDown' })

    expect(onSelectRow).not.toHaveBeenCalled()
  })
})

describe('RequestList empty-state backend nudge (issue #11)', () => {
  it('names the default backend and its address when passive health is red', async () => {
    renderList([], null, vi.fn(), status({ health: 'red' }))

    const nudge = await screen.findByRole('status')
    expect(nudge.textContent).toBe("ollama isn't responding at localhost:11434 — start it, or add a backend.")
    expect(screen.queryByText(NO_REQUESTS)).toBeNull()
  })

  it('nudges on the first-run probe answer, before any traffic has made health red', async () => {
    renderList([], null, vi.fn(), status({ firstRun: true, defaultBackendReachable: false, health: 'unknown' }))

    expect((await screen.findByRole('status')).textContent).toBe(
      "ollama isn't responding at localhost:11434 — start it, or add a backend.",
    )
  })

  it('reads the configured name and address, not the stock Ollama defaults', async () => {
    renderList([], null, vi.fn(), status({ backendName: 'lmstudio', baseUrl: 'http://127.0.0.1:1234', health: 'red' }))

    expect((await screen.findByRole('status')).textContent).toBe(
      "lmstudio isn't responding at 127.0.0.1:1234 — start it, or add a backend.",
    )
  })

  // PR review — the probe answers once at startup and is never refreshed, so it must not
  // outlive a newer observation: start Ollama, make one successful request, reset the
  // session, and the empty list must not insist the backend still isn't responding.
  it('drops the first-run probe answer once a captured request has proved the backend green', async () => {
    renderList([], null, vi.fn(), status({ firstRun: true, defaultBackendReachable: false, health: 'green' }))

    expect(await screen.findByText(NO_REQUESTS)).toBeTruthy()
    expect(screen.queryByRole('status')).toBeNull()
  })

  it('keeps the plain empty state when the default backend is reachable', async () => {
    renderList([], null, vi.fn(), status({ health: 'green', firstRun: true, defaultBackendReachable: true }))

    expect(await screen.findByText(NO_REQUESTS)).toBeTruthy()
    expect(screen.queryByRole('status')).toBeNull()
  })

  it('keeps the plain empty state while the default backend has never been observed', async () => {
    renderList([], null, vi.fn(), status({ health: 'unknown' }))

    expect(await screen.findByText(NO_REQUESTS)).toBeTruthy()
    expect(screen.queryByRole('status')).toBeNull()
  })

  it('shows no nudge once there are rows — the failures are visible as rows by then', async () => {
    renderList([summary(1)], null, vi.fn(), status({ health: 'red' }))

    await waitFor(() => expect(screen.getByText('/api/chat/1')).toBeTruthy())
    expect(screen.queryByRole('status')).toBeNull()
  })
})
