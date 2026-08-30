import { createElement, type ReactNode } from 'react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, fireEvent, waitFor, cleanup } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { api } from '@/api/client'
import type { RequestDetail } from '@/api/types'
import { DetailPane } from './DetailPane'

/**
 * R24 — the raw-stream fallback regression. When extraction returns null there is no rendered
 * view and no Rendered/Raw toggle, so the response tab is *effectively* in raw mode even though
 * `responseDisplay` stays at its unreachable 'rendered' default. The "Raw stream" sub-toggle
 * must still swap in `responseRaw`, and the decode-truncation notice must follow the shown
 * body. These exercise the actual DetailPane tab/toggle interaction, not the notice component
 * or a body that already renders.
 */

afterEach(cleanup)

function detail(overrides: Partial<RequestDetail>): RequestDetail {
  return {
    id: 1,
    startedAt: '2026-08-28T00:00:01.0000000Z',
    sessionId: 1,
    backend: 'stub',
    tags: [],
    method: 'POST',
    path: '/api/chat',
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
    requestHeaders: null,
    responseHeaders: null,
    requestBody: null,
    responseBody: null,
    responseRaw: null,
    ...overrides,
  }
}

function renderPane(data: RequestDetail) {
  vi.spyOn(api, 'getRequest').mockResolvedValue(data)
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: Infinity, staleTime: Infinity } },
  })
  const wrapper = ({ children }: { children: ReactNode }) =>
    createElement(QueryClientProvider, { client: queryClient }, children)
  return render(createElement(DetailPane, { id: data.id }), { wrapper })
}

async function openResponseTab() {
  fireEvent.click(await screen.findByRole('tab', { name: 'Response' }))
}

describe('DetailPane — raw-stream fallback (R24)', () => {
  it('shows a visible failure notice when clipboard access rejects', async () => {
    vi.spyOn(api, 'getStatus').mockResolvedValue({
      name: 'vessel', version: '0.1.0', listen: '127.0.0.1:4550', defaultBackend: 'stub',
      backends: [{ name: 'stub', baseUrl: 'http://localhost:11434', type: 'ollama', default: true, health: { state: 'unknown', lastSeenAt: null } }],
      capture: { recording: true }, mcp: { enabled: true }, listenSecurity: { isNonLoopback: false, isContainer: false }, serverRunId: 'run',
      setup: { firstRun: false, defaultBackendReachable: null },
    })
    vi.spyOn(api, 'getReplays').mockResolvedValue([])
    Object.defineProperty(navigator, 'clipboard', {
      configurable: true,
      value: { writeText: vi.fn().mockRejectedValue(new Error('clipboard denied')) },
    })
    renderPane(detail({ requestBody: { text: '{"model":"m"}' } }))

    fireEvent.click(await screen.findByRole('tab', { name: 'Request' }))
    fireEvent.click(await screen.findByRole('button', { name: 'Copy as curl' }))

    expect((await screen.findByRole('alert')).textContent).toContain('Could not copy curl')
  })

  // The review's exact repro: an unknown-format streamed response (three NDJSON lines in
  // responseRaw, responseBody null). "Raw stream" must show them, not "No response body".
  it('shows the raw stream for an unknown-format streamed response when Raw stream is selected', async () => {
    const raw = '{"a":1}\n{"b":2}\n{"c":3}'
    renderPane(detail({ streamed: true, format: 'raw', responseBody: null, responseRaw: { text: raw } }))

    await openResponseTab()

    // Reassembled (the default) has nothing to show for a streamed row (responseBody is null).
    expect(screen.getByText('No response body')).toBeTruthy()

    fireEvent.click(screen.getByText('Raw stream'))

    await waitFor(() => expect(screen.queryByText('No response body')).toBeNull())
    // NDJSON isn't a single JSON document, so PrettyJson renders it verbatim (no reflow).
    expect(screen.getByText(/\{"a":1\}/)).toBeTruthy()
    expect(screen.getByText(/\{"c":3\}/)).toBeTruthy()
  })

  // A known format whose extraction fails (non-streamed): no rendered view, no stream toggle,
  // and the raw JSON body shows directly via the PrettyJson fallback.
  it('falls back to the raw JSON body for a known format whose extraction fails', async () => {
    renderPane(
      detail({
        format: 'openai-chat',
        streamed: false,
        responseBody: { text: 'this is not parseable openai json' },
        responseRaw: null,
      }),
    )

    await openResponseTab()

    expect(screen.queryByText('No response body')).toBeNull()
    expect(screen.getByText(/this is not parseable openai json/)).toBeTruthy()
  })

  // The decode-truncation notice must follow the *selected* raw stream, not a stale payload.
  it('shows the decode-truncation notice on the selected raw stream', async () => {
    renderPane(
      detail({
        streamed: true,
        format: 'raw',
        responseBody: null,
        responseRaw: { text: '{"partial":true}', decodeTruncated: true },
      }),
    )

    await openResponseTab()
    // Reassembled shows the untruncated (null) body — no notice yet.
    expect(screen.queryByRole('alert')).toBeNull()

    fireEvent.click(screen.getByText('Raw stream'))

    await waitFor(() => expect(screen.getByRole('alert')).toBeTruthy())
    expect(screen.getByText(/display decode limit reached/)).toBeTruthy()
  })

  // The normal-body warning path is unchanged: a non-streamed decode-truncated body shows the
  // notice with no toggling required.
  it('still shows the decode-truncation notice for a normal non-streamed body', async () => {
    renderPane(
      detail({
        streamed: false,
        format: 'raw',
        responseBody: { text: '{"partial":true}', decodeTruncated: true },
        responseRaw: null,
      }),
    )

    await openResponseTab()

    expect(screen.getByRole('alert')).toBeTruthy()
    expect(screen.getByText(/display decode limit reached/)).toBeTruthy()
  })
})
