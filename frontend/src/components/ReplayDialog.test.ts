import { createElement } from 'react'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { api } from '@/api/client'
import type { RequestDetail, StatusBackend } from '@/api/types'
import { ReplayDialog } from './ReplayDialog'

afterEach(cleanup)

const backends: StatusBackend[] = [
  { name: 'openai-source', baseUrl: 'http://localhost:1', type: 'openai', default: true, health: { state: 'unknown', lastSeenAt: null } },
  { name: 'anthropic-target', baseUrl: 'http://localhost:2', type: 'anthropic', default: false, health: { state: 'unknown', lastSeenAt: null } },
]

function detail(overrides: Partial<RequestDetail> = {}): RequestDetail {
  return {
    id: 1, startedAt: '2026-08-29T12:00:00Z', sessionId: 1, backend: 'openai-source', tags: [], method: 'POST',
    path: '/v1/messages', format: 'anthropic-messages', model: 'before', statusCode: 200, error: null,
    streamed: false, replayOf: null, durationMs: 1, ttftMs: null, vesselOverheadMs: 1, tokPerSec: null,
    tokensIn: null, tokensOut: null, tokensCachedRead: null, tokensCachedWrite: null, tokensEstimated: false,
    stopReason: null, warnings: [], truncated: false, requestHeaders: null, responseHeaders: null,
    requestBody: { text: '{}' }, responseBody: null, responseRaw: null, ...overrides,
  }
}

describe('ReplayDialog', () => {
  it('reconciles a stale incompatible source backend to the first allowed target', async () => {
    const replay = vi.spyOn(api, 'replay').mockResolvedValue(undefined)
    render(createElement(ReplayDialog, { detail: detail(), backends, open: true, onClose: () => undefined }))

    expect((screen.getByLabelText('Backend') as HTMLSelectElement).value).toBe('anthropic-target')
    fireEvent.click(screen.getByRole('button', { name: 'Replay' }))
    await waitFor(() => expect(replay).toHaveBeenCalledWith(1, { backend: 'anthropic-target' }))
  })

  it('treats a blank model as no override and disables override for raw rows', async () => {
    const replay = vi.spyOn(api, 'replay').mockResolvedValue(undefined)
    const sourceOnly = [backends[0]]
    const rendered = render(createElement(ReplayDialog, {
      detail: detail({ format: 'openai-chat', path: '/v1/chat/completions' }), backends: sourceOnly, open: true, onClose: () => undefined,
    }))
    fireEvent.change(screen.getByLabelText('Model override'), { target: { value: '   ' } })
    fireEvent.click(screen.getByRole('button', { name: 'Replay' }))
    await waitFor(() => expect(replay).toHaveBeenCalledWith(1, {}))

    rendered.rerender(createElement(ReplayDialog, {
      detail: detail({ format: 'raw', path: '/custom' }), backends: sourceOnly, open: true, onClose: () => undefined,
    }))
    expect((screen.getByPlaceholderText('before') as HTMLInputElement).disabled).toBe(true)
  })
})
