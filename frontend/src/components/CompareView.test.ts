import { createElement, type ReactNode } from 'react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { api } from '@/api/client'
import type { RequestDetail } from '@/api/types'
import { CompareView, MetricDelta } from './CompareView'
import { formatMs } from '@/lib/format'

afterEach(cleanup)

function detail(id: number, model: string, replayOf: number | null): RequestDetail {
  return {
    id, startedAt: '2026-08-29T12:00:00Z', sessionId: 1, backend: 'stub', tags: [], method: 'POST',
    path: '/v1/chat/completions', format: 'openai-chat', model, statusCode: 200, error: null,
    streamed: false, replayOf, durationMs: id === 1 ? 2500 : 1000, ttftMs: null, vesselOverheadMs: 1,
    tokPerSec: null, tokensIn: 2, tokensOut: 3, tokensCachedRead: null, tokensCachedWrite: null,
    tokensEstimated: false, stopReason: 'stop', warnings: [], truncated: false,
    requestHeaders: null, responseHeaders: null,
    requestBody: { text: JSON.stringify({ model, messages: [{ role: 'user', content: 'one shared request' }] }) },
    responseBody: { text: JSON.stringify({ choices: [{ message: { role: 'assistant', content: `response ${id}` } }] }) },
    responseRaw: null,
  }
}

function wrapper() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return ({ children }: { children: ReactNode }) => createElement(QueryClientProvider, { client: queryClient }, children)
}

describe('CompareView', () => {
  it('renders one request, the model diff and both responses for a direct pair', async () => {
    vi.spyOn(api, 'getRequest').mockImplementation(async (id) => id === 1 ? detail(1, 'before', null) : detail(2, 'after', 1))
    render(createElement(CompareView, { originalId: 1, replayId: 2, onClose: () => undefined }), { wrapper: wrapper() })

    expect(await screen.findByText('Request')).toBeTruthy()
    expect(screen.getAllByText('one shared request')).toHaveLength(1)
    const modelDiff = screen.getByText('model').closest('div')
    expect(modelDiff?.textContent).toContain('"before"')
    expect(modelDiff?.textContent).toContain('"after"')
    expect(screen.getByText('response 1')).toBeTruthy()
    expect(screen.getByText('response 2')).toBeTruthy()
  })

  it('formats a negative multi-second delta with magnitude then sign', () => {
    render(createElement(MetricDelta, { label: 'Duration', a: '2.50s', b: '1.00s', delta: -1500, formatDelta: formatMs }))
    expect(screen.getByText('Δ −1.50s')).toBeTruthy()
  })

  it('#28 combines a recorded dialect fix-up into one "(auto)" row instead of two undefined rows', async () => {
    const original = { ...detail(1, 'm', null), requestBody: { text: JSON.stringify({ model: 'm', max_tokens: 2048 }) } }
    const replay = {
      ...detail(2, 'm', 1),
      requestBody: { text: JSON.stringify({ model: 'm', max_completion_tokens: 2048 }) },
      requestHeaders: { 'X-Vessel-Replay-Fixups': ['openai-chat:max_tokens->max_completion_tokens'] },
    }
    vi.spyOn(api, 'getRequest').mockImplementation(async (id) => id === 1 ? original : replay)
    render(createElement(CompareView, { originalId: 1, replayId: 2, onClose: () => undefined }), { wrapper: wrapper() })

    const row = (await screen.findByText('max_tokens → max_completion_tokens')).closest('div')
    expect(row?.textContent).toBe('max_tokens → max_completion_tokens2048 (auto)')
    expect(screen.queryByText('max_tokens')).toBeNull()
    expect(screen.queryByText('max_completion_tokens')).toBeNull()
  })

  it('#28 never labels a rename "(auto)" without the recorded fix-up header', async () => {
    const original = { ...detail(1, 'm', null), requestBody: { text: JSON.stringify({ model: 'm', max_tokens: 2048 }) } }
    const replay = { ...detail(2, 'm', 1), requestBody: { text: JSON.stringify({ model: 'm', max_completion_tokens: 2048 }) } }
    vi.spyOn(api, 'getRequest').mockImplementation(async (id) => id === 1 ? original : replay)
    render(createElement(CompareView, { originalId: 1, replayId: 2, onClose: () => undefined }), { wrapper: wrapper() })

    expect(await screen.findByText('max_tokens')).toBeTruthy()
    expect(screen.getByText('max_completion_tokens')).toBeTruthy()
    expect(screen.queryByText(/\(auto\)/)).toBeNull()
  })

  it('#28 still shows a recorded fix-up, from the original value, when the replay body itself did not parse', async () => {
    const original = { ...detail(1, 'm', null), requestBody: { text: JSON.stringify({ model: 'm', max_tokens: 2048 }) } }
    const replay = {
      ...detail(2, 'm', 1),
      requestBody: { text: undefined, decodeTruncated: true },
      requestHeaders: { 'X-Vessel-Replay-Fixups': ['openai-chat:max_tokens->max_completion_tokens'] },
    }
    vi.spyOn(api, 'getRequest').mockImplementation(async (id) => id === 1 ? original : replay)
    render(createElement(CompareView, { originalId: 1, replayId: 2, onClose: () => undefined }), { wrapper: wrapper() })

    const row = (await screen.findByText('max_tokens → max_completion_tokens')).closest('div')
    expect(row?.textContent).toBe('max_tokens → max_completion_tokens2048 (auto)')
  })
})
