import { createElement } from 'react'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { api } from '@/api/client'
import type { RequestDetail, StatusBackend } from '@/api/types'
import { ReplayDialog } from './ReplayDialog'

afterEach(cleanup)

const backends: StatusBackend[] = [
  { name: 'openai-source', baseUrl: 'http://localhost:1', type: 'openai', default: true, health: { state: 'unknown', lastSeenAt: null }, requiresAuth: false },
  { name: 'anthropic-target', baseUrl: 'http://localhost:2', type: 'anthropic', default: false, health: { state: 'unknown', lastSeenAt: null }, requiresAuth: false },
]

function detail(overrides: Partial<RequestDetail> = {}): RequestDetail {
  return {
    id: 1, startedAt: '2026-08-29T12:00:00Z', sessionId: 1, backend: 'openai-source', tags: [], method: 'POST',
    path: '/v1/messages', format: 'anthropic-messages', model: 'before', statusCode: 200, error: null,
    streamed: false, replayOf: null, replayGroup: null, replayPatch: null, score: null, durationMs: 1, ttftMs: null, vesselOverheadMs: 1, tokPerSec: null,
    tokensIn: null, tokensOut: null, tokensCachedRead: null, tokensCachedWrite: null, tokensEstimated: false,
    stopReason: null, warnings: [], truncated: false, requestHeaders: null, responseHeaders: null,
    requestBody: { text: '{}' }, responseBody: null, responseRaw: null, ...overrides,
  }
}

describe('ReplayDialog', () => {
  it('reconciles a stale incompatible source backend to the first allowed target', async () => {
    const replay = vi.spyOn(api, 'replay').mockResolvedValue({ replayGroup: 'fan0', count: 1 })
    render(createElement(ReplayDialog, { detail: detail(), backends, open: true, onClose: () => undefined }))

    expect((screen.getByLabelText('Backend') as HTMLSelectElement).value).toBe('anthropic-target')
    fireEvent.click(screen.getByRole('button', { name: 'Replay' }))
    await waitFor(() => expect(replay).toHaveBeenCalledWith(1, { backend: 'anthropic-target' }))
  })

  // #115 — the TypeSafe preset is `auto`, so an OpenAI-format capture never lists it as a
  // target, while its own System One rows still do.
  it('offers an auto-typed TypeSafe backend only to its own System One rows', () => {
    const typesafe: StatusBackend = { ...backends[0], name: 'typesafe', baseUrl: 'https://api.typesafe.ai', type: 'auto', default: false, requiresAuth: true }
    const targets = () => Array.from((screen.getByLabelText('Backend') as HTMLSelectElement).options).map((o) => o.value)

    const rendered = render(createElement(ReplayDialog, {
      detail: detail({ format: 'openai-chat', path: '/v1/chat/completions' }), backends: [backends[0], typesafe], open: true, onClose: () => undefined,
    }))
    expect(targets()).toEqual(['openai-source'])

    rendered.rerender(createElement(ReplayDialog, {
      detail: detail({ backend: 'typesafe', format: 'typesafe-systemone', path: '/v1/systemone' }), backends: [backends[0], typesafe], open: true, onClose: () => undefined,
    }))
    expect(targets()).toEqual(['typesafe'])
  })

  it('treats a blank model as no override and disables override for raw rows', async () => {
    const replay = vi.spyOn(api, 'replay').mockResolvedValue({ replayGroup: 'fan0', count: 1 })
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

  it('#48 fans one variation per model row and counts the paid calls before firing', async () => {
    const replay = vi.spyOn(api, 'replay').mockResolvedValue({ replayGroup: 'fan0', count: 2 })
    const keyed: StatusBackend[] = [backends[0], { ...backends[1], requiresAuth: true }]
    render(createElement(ReplayDialog, { detail: detail(), backends: keyed, open: true, onClose: () => undefined }))

    fireEvent.click(screen.getByRole('button', { name: 'Models' }))
    fireEvent.click(screen.getByRole('button', { name: 'Add model' }))
    fireEvent.change(screen.getByLabelText('Model 1'), { target: { value: 'haiku' } })
    fireEvent.change(screen.getByLabelText('Model 2'), { target: { value: 'sonnet' } })

    expect(screen.getByText('Sends 2 requests · 2 to keyed backends: anthropic-target')).toBeTruthy()
    fireEvent.click(screen.getByRole('button', { name: 'Replay ×2' }))
    await waitFor(() => expect(replay).toHaveBeenCalledWith(1, {
      variations: [
        { backend: 'anthropic-target', model: 'haiku' },
        { backend: 'anthropic-target', model: 'sonnet' },
      ],
    }))
  })

  it('#48 fans one variation per comma-separated parameter value', async () => {
    const replay = vi.spyOn(api, 'replay').mockResolvedValue({ replayGroup: 'fan0', count: 3 })
    render(createElement(ReplayDialog, { detail: detail(), backends, open: true, onClose: () => undefined }))

    fireEvent.click(screen.getByRole('button', { name: 'Params' }))
    fireEvent.change(screen.getByPlaceholderText('0.2, 0.7, 1.0'), { target: { value: '0.2, 0.7, 1.0' } })

    // Local-only fans say nothing about keys, because nothing is spent.
    expect(screen.getByText('Sends 3 requests')).toBeTruthy()
    fireEvent.click(screen.getByRole('button', { name: 'Replay ×3' }))
    await waitFor(() => expect(replay).toHaveBeenCalledWith(1, {
      variations: [
        { backend: 'anthropic-target', params: { temperature: 0.2 } },
        { backend: 'anthropic-target', params: { temperature: 0.7 } },
        { backend: 'anthropic-target', params: { temperature: 1 } },
      ],
    }))
  })

  it('#48 nests an ollama sampler under options, where a merge patch keeps its siblings', async () => {
    const replay = vi.spyOn(api, 'replay').mockResolvedValue({ replayGroup: 'fan0', count: 1 })
    const ollama: StatusBackend[] = [{ ...backends[0], name: 'ollama', type: 'ollama' }]
    render(createElement(ReplayDialog, {
      detail: detail({ backend: 'ollama', format: 'ollama-chat', path: '/api/chat' }),
      backends: ollama, open: true, onClose: () => undefined,
    }))

    fireEvent.click(screen.getByRole('button', { name: 'Params' }))
    fireEvent.change(screen.getByPlaceholderText('0.2, 0.7, 1.0'), { target: { value: '0.9' } })
    fireEvent.click(screen.getByRole('button', { name: 'Replay ×1' }))
    await waitFor(() => expect(replay).toHaveBeenCalledWith(1, {
      variations: [{ params: { options: { temperature: 0.9 } } }],
    }))
  })

  it('uses Responses token-limit parameters and sends max_output_tokens in the replay patch', async () => {
    const replay = vi.spyOn(api, 'replay').mockResolvedValue({ replayGroup: 'fan0', count: 1 })
    render(createElement(ReplayDialog, {
      detail: detail({ format: 'openai-responses', path: '/v1/responses' }),
      backends: [backends[0]], open: true, onClose: () => undefined,
    }))

    fireEvent.click(screen.getByRole('button', { name: 'Params' }))

    expect(screen.getByRole('option', { name: 'max_output_tokens' })).toBeTruthy()
    expect(screen.queryByRole('option', { name: 'max_tokens' })).toBeNull()
    expect(screen.queryByRole('option', { name: 'max_completion_tokens' })).toBeNull()

    fireEvent.change(screen.getByRole('combobox', { name: 'Parameter' }), {
      target: { value: 'max_output_tokens' },
    })
    fireEvent.change(screen.getByPlaceholderText('0.2, 0.7, 1.0'), {
      target: { value: '512' },
    })

    fireEvent.click(screen.getByRole('button', { name: 'Replay ×1' }))

    await waitFor(() => expect(replay).toHaveBeenCalledWith(1, {
      variations: [{ params: { max_output_tokens: 512 } }],
    }))
  })
})
