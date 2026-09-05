import { createElement, type ReactNode } from 'react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { api } from '@/api/client'
import type { RequestDetail } from '@/api/types'
import { CompareView, MetricCell } from './CompareView'
import { formatMs } from '@/lib/format'

afterEach(cleanup)

function detail(id: number, model: string, replayOf: number | null): RequestDetail {
  return {
    id, startedAt: '2026-08-29T12:00:00Z', sessionId: 1, backend: 'stub', tags: [], method: 'POST',
    path: '/v1/chat/completions', format: 'openai-chat', model, statusCode: 200, error: null,
    streamed: false, replayOf, durationMs: id === 1 ? 2500 : 1000, ttftMs: null, vesselOverheadMs: 1,
    tokPerSec: null, tokensIn: 2, tokensOut: 3, tokensCachedRead: null, tokensCachedWrite: null,
    tokensEstimated: false, stopReason: 'stop', warnings: [], truncated: false,
    replayGroup: replayOf == null ? null : 'fan0', replayPatch: null, score: null,
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
    render(createElement(CompareView, { originalId: 1, replayIds: [2], onClose: () => undefined }), { wrapper: wrapper() })

    expect(await screen.findByText('Request')).toBeTruthy()
    expect(screen.getAllByText('one shared request')).toHaveLength(1)
    const modelDiff = screen.getByText('model').closest('div')
    expect(modelDiff?.textContent).toContain('"before"')
    expect(modelDiff?.textContent).toContain('"after"')
    expect(screen.getByText('response 1')).toBeTruthy()
    expect(screen.getByText('response 2')).toBeTruthy()
    // #48 review — metrics are one table of a fixed row set whatever N is, pair included
    // (#49 added the Score row to it).
    expect(screen.getByText('Duration').closest('table')?.querySelectorAll('tbody tr')).toHaveLength(7)
  })

  it('formats a negative multi-second delta with magnitude then sign', () => {
    render(createElement(MetricCell, { value: '1.00s', delta: -1500, formatDelta: formatMs }))
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
    render(createElement(CompareView, { originalId: 1, replayIds: [2], onClose: () => undefined }), { wrapper: wrapper() })

    const row = (await screen.findByText('max_tokens → max_completion_tokens')).closest('div')
    expect(row?.textContent).toBe('max_tokens → max_completion_tokens2048 (auto)')
    expect(screen.queryByText('max_tokens')).toBeNull()
    expect(screen.queryByText('max_completion_tokens')).toBeNull()
  })

  it('#28 never labels a rename "(auto)" without the recorded fix-up header', async () => {
    const original = { ...detail(1, 'm', null), requestBody: { text: JSON.stringify({ model: 'm', max_tokens: 2048 }) } }
    const replay = { ...detail(2, 'm', 1), requestBody: { text: JSON.stringify({ model: 'm', max_completion_tokens: 2048 }) } }
    vi.spyOn(api, 'getRequest').mockImplementation(async (id) => id === 1 ? original : replay)
    render(createElement(CompareView, { originalId: 1, replayIds: [2], onClose: () => undefined }), { wrapper: wrapper() })

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
    render(createElement(CompareView, { originalId: 1, replayIds: [2], onClose: () => undefined }), { wrapper: wrapper() })

    const row = (await screen.findByText('max_tokens → max_completion_tokens')).closest('div')
    expect(row?.textContent).toBe('max_tokens → max_completion_tokens2048 (auto)')
  })

  it('#48 renders one column per fan member, labelled by the recorded patch', async () => {
    const members: Record<number, RequestDetail> = {
      1: detail(1, 'base', null),
      2: { ...detail(2, 'base', 1), replayPatch: '{"temperature":0.2}' },
      3: { ...detail(3, 'base', 1), replayPatch: '{"options":{"temperature":0.7}}' },
    }
    vi.spyOn(api, 'getRequest').mockImplementation(async (id) => members[id])
    render(createElement(CompareView, { originalId: 1, replayIds: [2, 3], onClose: () => undefined }), { wrapper: wrapper() })

    expect(await screen.findByText('Original #1')).toBeTruthy()
    expect(screen.getAllByText(/temperature 0\.2/).length).toBeGreaterThan(0)
    // The label is the patch's leaf, so a nested Ollama sampler reads the same as a flat one.
    expect(screen.getAllByText(/temperature 0\.7/).length).toBeGreaterThan(0)
    expect(screen.getByText('response 2')).toBeTruthy()
    expect(screen.getByText('response 3')).toBeTruthy()
  })

  it('#48 shows an in-flight member of the same fan as its own column', async () => {
    const members: Record<number, RequestDetail> = {
      1: detail(1, 'base', null),
      2: detail(2, 'base', 1),
      3: detail(3, 'base', 1),
    }
    vi.spyOn(api, 'getRequest').mockImplementation(async (id) => members[id])
    render(createElement(CompareView, {
      originalId: 1,
      replayIds: [2, 3],
      inFlight: [
        { seq: 9, startedAt: '2026-08-29T12:00:00Z', sessionId: 1, sessionName: null, method: 'POST', path: '/v1/chat/completions', backend: 'stub', tags: [], replayOf: 1, replayGroup: 'fan0' },
        { seq: 10, startedAt: '2026-08-29T12:00:00Z', sessionId: 1, sessionName: null, method: 'POST', path: '/v1/chat/completions', backend: 'stub', tags: [], replayOf: 99, replayGroup: 'other' },
      ],
      onClose: () => undefined,
    }), { wrapper: wrapper() })

    expect(await screen.findByText('Replay #9…')).toBeTruthy()
    expect(screen.getByText('In flight…')).toBeTruthy()
    expect(screen.queryByText('Replay #10…')).toBeNull()
  })

  // #49 — the control lives in the reserved header slot, on every scorable column.
  it('sets a score, clears it by clicking the current value, and offers the original a control too', async () => {
    const setScore = vi.spyOn(api, 'setScore').mockResolvedValue(undefined)
    vi.spyOn(api, 'getRequest').mockImplementation(async (id) =>
      id === 1 ? detail(1, 'before', null) : { ...detail(2, 'after', 1), score: 3 })
    render(createElement(CompareView, { originalId: 1, replayIds: [2], onClose: () => undefined }), { wrapper: wrapper() })

    // Clicking the value it already holds is the clear gesture; anything else sets.
    const replay = await screen.findByRole('group', { name: 'Score Replay #2' })
    fireEvent.click(within(replay).getByRole('button', { name: 'Score 3' }))
    await waitFor(() => expect(setScore).toHaveBeenCalledWith(2, null))

    fireEvent.click(within(replay).getByRole('button', { name: 'Score 5' }))
    await waitFor(() => expect(setScore).toHaveBeenCalledWith(2, 5))

    const original = screen.getByRole('group', { name: 'Score Original #1' })
    fireEvent.click(within(original).getByRole('button', { name: 'Score 4' }))
    await waitFor(() => expect(setScore).toHaveBeenCalledWith(1, 4))
  })

  it('leaves an in-flight column unscorable — there is nothing to score yet', async () => {
    vi.spyOn(api, 'getRequest').mockImplementation(async (id) => detail(id, 'm', id === 1 ? null : 1))
    render(createElement(CompareView, {
      originalId: 1,
      replayIds: [2],
      inFlight: [{ seq: 9, startedAt: '2026-08-29T12:00:00Z', sessionId: 1, sessionName: null, method: 'POST', path: '/x', backend: 'stub', tags: [], replayOf: 1, replayGroup: 'fan0' }],
      onClose: () => undefined,
    }), { wrapper: wrapper() })

    expect(await screen.findByText('Replay #9…')).toBeTruthy()
    expect(screen.queryByRole('group', { name: 'Score Replay #9…' })).toBeNull()
  })

  it('#49 scores the focused column from the keyboard, and ignores keys while an input has focus', async () => {
    const setScore = vi.spyOn(api, 'setScore').mockResolvedValue(undefined)
    vi.spyOn(api, 'getRequest').mockImplementation(async (id) => detail(id, 'm', id === 1 ? null : 1))
    render(createElement(CompareView, { originalId: 1, replayIds: [2, 3], onClose: () => undefined }), { wrapper: wrapper() })
    await screen.findByText('Original #1')

    // Focus starts on the original; → walks to the first member.
    fireEvent.keyDown(window, { key: '2' })
    await waitFor(() => expect(setScore).toHaveBeenCalledWith(1, 2))
    fireEvent.keyDown(window, { key: 'ArrowRight' })
    fireEvent.keyDown(window, { key: '5' })
    await waitFor(() => expect(setScore).toHaveBeenCalledWith(2, 5))
    fireEvent.keyDown(window, { key: '0' })
    await waitFor(() => expect(setScore).toHaveBeenCalledWith(2, null))

    setScore.mockClear()
    const input = document.createElement('input')
    document.body.appendChild(input)
    input.focus()
    fireEvent.keyDown(window, { key: '4' })
    expect(setScore).not.toHaveBeenCalled()
    input.remove()
  })

  // #48 review — the panel's one job is to show what changed, and for a params fan that is
  // the patch's leaf, not the whole `options` object it sits in.
  it('rows a params fan by the patch leaf path, with the original read from its own body', async () => {
    const ollama = (id: number, temperature: number | null, patch: string | null): RequestDetail => ({
      ...detail(id, 'qwen', id === 1 ? null : 1),
      replayPatch: patch,
      requestBody: {
        text: JSON.stringify({
          model: 'qwen',
          messages: [{ role: 'user', content: 'one shared request' }],
          options: { seed: 42, ...(temperature === null ? {} : { temperature }) },
        }),
      },
    })
    const rows: Record<number, RequestDetail> = {
      1: ollama(1, 0, null),
      2: ollama(2, 0.5, '{"options":{"temperature":0.5}}'),
      3: ollama(3, null, '{"options":{"temperature":null}}'),
    }
    vi.spyOn(api, 'getRequest').mockImplementation(async (id) => rows[id])
    render(createElement(CompareView, { originalId: 1, replayIds: [2, 3], onClose: () => undefined }), { wrapper: wrapper() })

    const row = (await screen.findByText('options.temperature')).closest('tr')
    expect(row?.textContent).toContain('0.5')
    // `seed` is a sibling under the same `options` object and did not vary — no row for it.
    expect(screen.queryByText('options.seed')).toBeNull()
    // A null patch leaf deletes the key rather than setting it to null.
    expect(row?.textContent).toContain('(removed)')
  })

  // Review P1 — a member that finishes while Compare is open must become a column, not
  // vanish with its pending one. The selection is frozen; the replay list is not.
  it('picks up a fan member that completes while the view is open', async () => {
    const summary = (id: number) => ({ ...detail(id, 'm', 1), replayGroup: 'fan0' })
    vi.spyOn(api, 'getRequest').mockImplementation(async (id) => detail(id, 'm', id === 1 ? null : 1))
    const getReplays = vi.spyOn(api, 'getReplays').mockResolvedValue([summary(2)])
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    const wrap = ({ children }: { children: ReactNode }) =>
      createElement(QueryClientProvider, { client }, children)
    render(createElement(CompareView, { originalId: 1, replayIds: [2], onClose: () => undefined }), { wrapper: wrap })

    expect(await screen.findByText('Replay #2')).toBeTruthy()
    expect(screen.queryByText('Replay #3')).toBeNull()

    // #3 lands: the same invalidation a completion already fires brings it in.
    getReplays.mockResolvedValue([summary(2), summary(3)])
    await client.invalidateQueries({ queryKey: ['replays', 1] })
    expect(await screen.findByText('Replay #3')).toBeTruthy()
  })

  // Review P2 — a params fan can patch the very key the dialect fix-up then renames; the
  // panel must show the patched value, not five copies of the original.
  it('keeps a patched value when the dialect fix-up renamed its key', async () => {
    const original: RequestDetail = {
      ...detail(1, 'm', null),
      requestBody: { text: JSON.stringify({ model: 'm', messages: [], max_tokens: 2048 }) },
    }
    const replay: RequestDetail = {
      ...detail(2, 'm', 1),
      replayPatch: '{"max_tokens":512}',
      requestHeaders: { 'X-Vessel-Replay-Fixups': ['openai-chat:max_tokens->max_completion_tokens'] },
      requestBody: { text: JSON.stringify({ model: 'm', messages: [], max_completion_tokens: 512 }) },
    }
    vi.spyOn(api, 'getRequest').mockImplementation(async (id) => (id === 1 ? original : replay))
    render(createElement(CompareView, { originalId: 1, replayIds: [2], onClose: () => undefined }), { wrapper: wrapper() })

    const row = (await screen.findByText('max_tokens → max_completion_tokens')).closest('div')
    expect(row?.textContent).toContain('2048')
    expect(row?.textContent).toContain('512')
    expect(row?.textContent).toContain('(auto)')
  })

  // Review — neither side of a rename may be filled in from the other: that turns an added
  // limit, and a patch whose body could not be read, into apparent no-ops.
  it('shows an absent original as — and falls back to the recorded patch, not the other side', async () => {
    const fixups = { 'X-Vessel-Replay-Fixups': ['openai-chat:max_tokens->max_completion_tokens'] }
    const added: RequestDetail = {
      ...detail(2, 'm', 1),
      replayPatch: '{"max_tokens":512}',
      requestHeaders: fixups,
      requestBody: { text: JSON.stringify({ model: 'm', messages: [], max_completion_tokens: 512 }) },
    }
    const bare: RequestDetail = {
      ...detail(1, 'm', null),
      requestBody: { text: JSON.stringify({ model: 'm', messages: [] }) },
    }
    vi.spyOn(api, 'getRequest').mockImplementation(async (id) => (id === 1 ? bare : added))
    const { unmount } = render(
      createElement(CompareView, { originalId: 1, replayIds: [2], onClose: () => undefined }),
      { wrapper: wrapper() },
    )

    const addedRow = (await screen.findByText('max_tokens → max_completion_tokens')).closest('div')
    expect(addedRow?.textContent).toContain('—')
    expect(addedRow?.textContent).toContain('512')
    unmount()

    // The replay body is unreadable, but the recorded patch still knows what was set.
    const unreadable: RequestDetail = { ...added, requestBody: null }
    const withLimit: RequestDetail = {
      ...detail(1, 'm', null),
      requestBody: { text: JSON.stringify({ model: 'm', messages: [], max_tokens: 2048 }) },
    }
    vi.spyOn(api, 'getRequest').mockImplementation(async (id) => (id === 1 ? withLimit : unreadable))
    render(createElement(CompareView, { originalId: 1, replayIds: [2], onClose: () => undefined }), { wrapper: wrapper() })

    const patchedRow = (await screen.findByText('max_tokens → max_completion_tokens')).closest('div')
    expect(patchedRow?.textContent).toContain('2048')
    expect(patchedRow?.textContent).toContain('512')
  })
})
