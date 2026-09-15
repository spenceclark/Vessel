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
      backends: [{ name: 'stub', baseUrl: 'http://localhost:11434', type: 'ollama', default: true, health: { state: 'unknown', lastSeenAt: null }, requiresAuth: false }],
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

  // #48 review — two fans of one original must be told apart: what varied, and when.
  it('labels each replay fan by its varied dimension and age', async () => {
    const member = (id: number, group: string, model: string | null, patch: string | null) => ({
      id, startedAt: new Date(Date.now() - 120_000).toISOString(), sessionId: 1, backend: 'stub', tags: [],
      method: 'POST', path: '/api/chat', format: 'ollama-chat', model, statusCode: 200, error: null,
      streamed: false, replayOf: 1, replayGroup: group, replayPatch: patch, score: null, durationMs: 10, ttftMs: null,
      vesselOverheadMs: 1, tokPerSec: null, tokensIn: null, tokensOut: null, tokensCachedRead: null,
      tokensCachedWrite: null, tokensEstimated: false, stopReason: null, warnings: [], truncated: false,
    })
    vi.spyOn(api, 'getReplays').mockResolvedValue([
      member(2, 'fanA', 'qwen', null),
      member(3, 'fanA', 'llama', null),
      member(4, 'fanB', 'base', '{"temperature":0.7}'),
      member(5, 'fanB', 'base', '{"temperature":0.9}'),
      member(6, 'fanC', 'base', null),
      member(7, 'fanC', 'base', null),
      member(8, null as unknown as string, 'base', null),
    ])
    renderPane(detail({ model: 'base' }))

    // The counts are stated once, in the header; rows say what varied and when.
    expect(await screen.findByText('Replays · 4 fans, 7 replays')).toBeTruthy()
    expect(screen.getByText('models · 2 · 2m ago')).toBeTruthy()
    expect(screen.getByText('params · 2 · 2m ago')).toBeTruthy()
    // Same model twice is a variance run, not a model comparison.
    expect(screen.getByText('×2 same model · 2 · 2m ago')).toBeTruthy()
    // A groupless single replay is a row like any other.
    expect(screen.getByText('#8 · single · 2m ago')).toBeTruthy()
    expect(screen.getAllByRole('button', { name: 'Compare' })).toHaveLength(4)
  })
})

describe('DetailPane — rate limits (#101)', () => {
  it('renders provider-specific rate-limit headers case-insensitively', async () => {
    renderPane(
      detail({
        responseHeaders: {
          'anthropic-ratelimit-requests-limit': ['50'],
          'anthropic-ratelimit-requests-remaining': ['49'],
          'anthropic-ratelimit-requests-reset': ['2026-09-15T00:01:00Z'],
          'anthropic-ratelimit-input-tokens-limit': ['1000'],
          'anthropic-ratelimit-output-tokens-remaining': ['900'],
          'X-RateLimit-Limit-Tokens': ['2000'],
          'X-RateLimit-Remaining-Tokens': ['1900'],
        },
      }),
    )

    expect(await screen.findByText('Rate limits')).toBeTruthy()

    expect(screen.getByText('requests limit')).toBeTruthy()
    expect(screen.getByText('50')).toBeTruthy()
    expect(screen.getByText('requests remaining')).toBeTruthy()
    expect(screen.getByText('49')).toBeTruthy()

    expect(screen.getByText('input-tokens limit')).toBeTruthy()
    expect(screen.getByText('1000')).toBeTruthy()

    expect(screen.getByText('output-tokens remaining')).toBeTruthy()
    expect(screen.getByText('900')).toBeTruthy()

    expect(screen.getByText('tokens limit')).toBeTruthy()
    expect(screen.getByText('2000')).toBeTruthy()
    expect(screen.getByText('tokens remaining')).toBeTruthy()
    expect(screen.getByText('1900')).toBeTruthy()
  })
})

describe('DetailPane — Tools tab (#81)', () => {
  const withTools = detail({
    id: 1,
    format: 'anthropic-messages',
    requestBody: {
      text: JSON.stringify({
        messages: [
          { role: 'user', content: 'go' },
          { role: 'assistant', content: [{ type: 'tool_use', id: 'a', name: 'read_file', input: {} }] },
          { role: 'user', content: [{ type: 'tool_result', tool_use_id: 'a', content: 'x' }] },
          {
            role: 'assistant',
            content: [
              { type: 'tool_use', id: 'b', name: 'read_file', input: {} },
              { type: 'server_tool_use', id: 's', name: 'web_search', input: { query: 'q' } },
            ],
          },
        ],
        tools: [
          { name: 'read_file', description: 'Read a file', input_schema: { type: 'object', properties: { path: { type: 'string' } }, required: ['path'] } },
          { name: 'write_file', input_schema: { type: 'object', properties: {} } },
          { type: 'web_search_20250305', name: 'web_search', max_uses: 3, allowed_domains: null },
        ],
      }),
    },
  })
  const withoutTools = detail({ id: 2, format: 'anthropic-messages', requestBody: { text: JSON.stringify({ messages: [{ role: 'user', content: 'hi' }] }) } })

  function renderSwitchable() {
    vi.spyOn(api, 'getRequest').mockImplementation(async (id: number) => (id === 1 ? withTools : withoutTools))
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: Infinity, staleTime: Infinity } } })
    const wrapper = ({ children }: { children: ReactNode }) => createElement(QueryClientProvider, { client: queryClient }, children)
    const view = render(createElement(DetailPane, { id: 1 }), { wrapper })
    return { rerender: (id: number) => view.rerender(createElement(DetailPane, { id })) }
  }

  it('hides the tab when the request declares no tools', async () => {
    renderPane(withoutTools)
    await screen.findByRole('tab', { name: 'Overview' })
    expect(screen.queryByRole('tab', { name: /Tools/ })).toBeNull()
  })

  it('shows the tab with the tool count', async () => {
    renderPane(withTools)
    expect(await screen.findByRole('tab', { name: 'Tools (3)' })).toBeTruthy()
  })

  it('counts calls from prior tool_use and server_tool_use turns', async () => {
    renderPane(withTools)
    fireEvent.click(await screen.findByRole('tab', { name: 'Tools (3)' }))

    expect(screen.getByText('2 calls')).toBeTruthy() // read_file
    expect(screen.getByText('0 calls')).toBeTruthy() // write_file
    expect(screen.getByText('1 call')).toBeTruthy() // web_search (server tool)
    expect(screen.getByText('server')).toBeTruthy()
    expect(screen.getByText('max_uses')).toBeTruthy()
    expect(screen.queryByText('allowed_domains')).toBeNull()
  })

  it('falls back to Overview when the selection changes to a request without tools', async () => {
    const { rerender } = renderSwitchable()
    fireEvent.click(await screen.findByRole('tab', { name: 'Tools (3)' }))
    expect(screen.getByRole('tab', { name: 'Tools (3)' }).getAttribute('aria-selected')).toBe('true')

    rerender(2)

    await waitFor(() => expect(screen.getByRole('tab', { name: 'Overview' }).getAttribute('aria-selected')).toBe('true'))
    expect(screen.queryByRole('tab', { name: /Tools/ })).toBeNull()
  })
})
