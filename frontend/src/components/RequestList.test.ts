import { createElement, type ReactNode } from 'react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, fireEvent, waitFor, cleanup } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { api } from '@/api/client'
import { EMPTY_FILTERS, type RequestListResponse, type Summary } from '@/api/types'
import type { Selection } from '@/App'
import { RequestList } from './RequestList'

/**
 * Issue #6 — ↑/↓ should move the selection to the prev/next request (email-client
 * pattern) rather than scrolling the pane, and must not hijack the search box while a
 * text input is focused.
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

function renderList(rows: Summary[], selection: Selection | null, onSelectRow: (id: number) => void) {
  const response: RequestListResponse = { rows, nextBefore: null }
  vi.spyOn(api, 'listRequests').mockResolvedValue(response)
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
