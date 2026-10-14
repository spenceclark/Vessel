import { describe, expect, it } from 'vitest'
import { findPreviousTurn } from './previousTurn'
import type { RenderedView, RenderMessage } from './types'

function msg(role: string, text: string): RenderMessage {
  return { role, blocks: [{ kind: 'text', text }] }
}

function view(messages: RenderMessage[], system?: string): RenderedView {
  return { system, messages, params: [] }
}

function loader(views: Record<number, RenderedView | null>) {
  const loaded: number[] = []
  const load = async (id: number) => {
    loaded.push(id)
    return views[id] ?? null
  }
  return { load, loaded }
}

/** #147 — the prefix matcher behind the Request tab's "previous turn". */
describe('findPreviousTurn', () => {
  const a1 = msg('user', 'A: task')
  const a2 = msg('assistant', 'A: calling tool')
  const a3 = msg('tool', 'A: tool result')
  const b1 = msg('user', 'B: task')
  const b2 = msg('assistant', 'B: calling tool')

  it("finds the same agent's previous turn through interleaved tagged traffic", async () => {
    // Newest first: #4 is agent B, #3 agent A's turn N-1, #2 B's first turn, #1 A's first turn.
    const { load, loaded } = loader({
      4: view([b1, b2, msg('tool', 'B: result')]),
      3: view([a1]),
      2: view([b1]),
      1: view([a1]),
    })
    const current = { tags: ['agent-a'], view: view([a1, a2, a3]) }
    const candidates = [
      { id: 4, tags: ['agent-b'] },
      { id: 3, tags: ['agent-a'] },
      { id: 2, tags: ['agent-b'] },
      { id: 1, tags: ['agent-a'] },
    ]

    expect(await findPreviousTurn(current, candidates, load)).toEqual({
      kind: 'match',
      id: 3,
      start: 0,
      carried: 1,
      systemChanged: false,
      toolsChanged: false,
    })
    // The whole same-tag group is searched for the longest prefix; agent B's rows never load.
    expect(loaded).toEqual([3, 1])
  })

  it('prefers the longest prefix over a newer, shorter retry', async () => {
    const c = msg('user', 'A: follow-up')
    const d = msg('assistant', 'A: answer')
    // #11 retried only the opening message; #10 is the turn this request actually extends.
    const { load } = loader({ 11: view([a1]), 10: view([a1, a2, c]) })
    const result = await findPreviousTurn(
      { tags: ['agent-a'], view: view([a1, a2, c, d, msg('user', 'A: next')]) },
      [{ id: 11, tags: ['agent-a'] }, { id: 10, tags: ['agent-a'] }],
      load,
    )
    expect(result).toMatchObject({ kind: 'match', id: 10, carried: 3 })
  })

  it('takes the newest of equally long prefixes', async () => {
    const { load } = loader({ 2: view([a1]), 1: view([a1]) })
    const result = await findPreviousTurn(
      { tags: [], view: view([a1, a2, a3]) },
      [{ id: 2, tags: [] }, { id: 1, tags: [] }],
      load,
    )
    expect(result).toMatchObject({ kind: 'match', id: 2 })
  })

  it('stops early on a prefix one message short of this request', async () => {
    const { load, loaded } = loader({ 3: view([a1, a2]), 2: view([a1]) })
    const result = await findPreviousTurn(
      { tags: [], view: view([a1, a2, a3]) },
      [{ id: 3, tags: [] }, { id: 2, tags: [] }],
      load,
    )
    expect(result).toMatchObject({ kind: 'match', id: 3, carried: 2 })
    expect(loaded).toEqual([3])
  })

  it('keeps a same-tag match over a longer other-tag one', async () => {
    const { load } = loader({ 2: view([a1, a2]), 1: view([a1]) })
    const result = await findPreviousTurn(
      { tags: ['agent-a'], view: view([a1, a2, a3]) },
      [{ id: 2, tags: ['agent-b'] }, { id: 1, tags: ['agent-a'] }],
      load,
    )
    expect(result).toMatchObject({ kind: 'match', id: 1, carried: 1 })
  })

  it('falls back to any tag when no same-tag candidate matches', async () => {
    const { load } = loader({ 5: view([a1]) })
    const result = await findPreviousTurn({ tags: [], view: view([a1, a2]) }, [{ id: 5, tags: ['other'] }], load)
    expect(result).toMatchObject({ kind: 'match', id: 5, carried: 1 })
  })

  it('treats tag order as irrelevant', async () => {
    const { load, loaded } = loader({ 2: view([a1]), 1: view([a1]) })
    const result = await findPreviousTurn(
      { tags: ['x', 'y'], view: view([a1, a2]) },
      [{ id: 2, tags: ['z'] }, { id: 1, tags: ['y', 'x'] }],
      load,
    )
    expect(result).toMatchObject({ kind: 'match', id: 1 })
    expect(loaded).toEqual([1])
  })

  it('reports a compacted history as rewritten rather than a false match', async () => {
    const { load } = loader({ 7: view([a1, a2, a3]) })
    const compacted = view([msg('user', 'Summary of the conversation so far'), msg('user', 'continue')])
    const result = await findPreviousTurn({ tags: ['agent-a'], view: compacted }, [{ id: 7, tags: ['agent-a'] }], load)
    expect(result).toEqual({ kind: 'rewritten', id: 7 })
  })

  it('does not match an exact retry', async () => {
    const { load } = loader({ 2: view([a1, a2]), 1: view([a1]) })
    const result = await findPreviousTurn(
      { tags: [], view: view([a1, a2]) },
      [{ id: 2, tags: [] }, { id: 1, tags: [] }],
      load,
    )
    expect(result).toMatchObject({ kind: 'match', id: 1, carried: 1 })
  })

  it('flags system prompt and tool changes', async () => {
    const tools = [{ name: 'read', kind: 'function' as const, params: [], rawJson: '{}' }]
    const { load } = loader({ 1: { ...view([a1], 'v1'), tools: [] } })
    const result = await findPreviousTurn({ tags: [], view: { ...view([a1, a2], 'v2'), tools } }, [{ id: 1, tags: [] }], load)
    expect(result).toMatchObject({ systemChanged: true, toolsChanged: true })
  })

  it('treats leading system messages as the system prompt, not the conversation', async () => {
    const { load } = loader({ 1: view([msg('system', 'v1'), a1]) })
    const result = await findPreviousTurn(
      { tags: [], view: view([msg('system', 'v2'), a1, a2]) },
      [{ id: 1, tags: [] }],
      load,
    )
    expect(result).toEqual({ kind: 'match', id: 1, start: 1, carried: 1, systemChanged: true, toolsChanged: false })
  })

  it('returns null with no candidates', async () => {
    const { load } = loader({})
    expect(await findPreviousTurn({ tags: ['agent-a'], view: view([a1]) }, [], load)).toBeNull()
  })

  it('returns null when only other-tag candidates exist and none match', async () => {
    const { load } = loader({ 1: view([b1]) })
    expect(await findPreviousTurn({ tags: ['agent-a'], view: view([a1, a2]) }, [{ id: 1, tags: ['agent-b'] }], load)).toBeNull()
  })

  it('skips candidates that do not render', async () => {
    const { load } = loader({ 2: null, 1: view([a1]) })
    const result = await findPreviousTurn({ tags: [], view: view([a1, a2]) }, [{ id: 2, tags: [] }, { id: 1, tags: [] }], load)
    expect(result).toMatchObject({ kind: 'match', id: 1 })
  })
})
