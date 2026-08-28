import { describe, expect, it } from 'vitest'
import { extractOllamaRequest, extractOllamaResponse } from './ollama'
import type { RequestDetail, BodyPayload } from '@/api/types'

/**
 * R09/R18 remainders — generate's fields are top-level (`thinking`, `images`), unlike
 * chat's per-message shape; the accumulation/extraction logic already proven for chat
 * needs its own coverage for generate's distinct wire shape.
 */

function detail(format: string, requestBody: BodyPayload | null, responseBody: BodyPayload | null): RequestDetail {
  return {
    id: 1,
    startedAt: '2026-08-28T00:00:00Z',
    sessionId: 1,
    backend: 'ollama',
    tags: [],
    method: 'POST',
    path: '/api/generate',
    format,
    model: 'qwen2.5:1.5b',
    statusCode: 200,
    error: null,
    streamed: false,
    replayOf: null,
    durationMs: 100,
    ttftMs: null,
    vesselOverheadMs: null,
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
    requestBody,
    responseBody,
    responseRaw: null,
  }
}

function body(text: string): BodyPayload {
  return { text }
}

describe('extractOllamaRequest — ollama-generate', () => {
  it('renders the prompt as a user message', () => {
    const view = extractOllamaRequest(detail('ollama-generate', body(JSON.stringify({ prompt: 'Hello there' })), null))
    expect(view?.messages).toEqual([{ role: 'user', blocks: [{ kind: 'markdown', text: 'Hello there' }] }])
  })

  it('R18 — a top-level images array produces image blocks alongside the prompt', () => {
    const view = extractOllamaRequest(
      detail('ollama-generate', body(JSON.stringify({ prompt: 'What is this?', images: ['aGVsbG8='] })), null),
    )
    expect(view?.messages).toHaveLength(1)
    expect(view?.messages[0].blocks).toEqual([
      { kind: 'markdown', text: 'What is this?' },
      { kind: 'image', label: 'image', source: { kind: 'embedded', dataUri: 'data:image/png;base64,aGVsbG8=' } },
    ])
  })

  it('an empty prompt with an image still produces a message (the preview path must be reachable)', () => {
    const view = extractOllamaRequest(detail('ollama-generate', body(JSON.stringify({ prompt: '', images: ['aGVsbG8='] })), null))
    expect(view?.messages).toHaveLength(1)
    expect(view?.messages[0].blocks).toEqual([
      { kind: 'image', label: 'image', source: { kind: 'embedded', dataUri: 'data:image/png;base64,aGVsbG8=' } },
    ])
  })

  it('a malformed image entry degrades to an unknown source rather than throwing', () => {
    const view = extractOllamaRequest(
      detail('ollama-generate', body(JSON.stringify({ prompt: 'x', images: [42, null, ''] })), null),
    )
    expect(view?.messages[0].blocks).toEqual([
      { kind: 'markdown', text: 'x' },
      { kind: 'image', label: 'image', source: { kind: 'unknown' } },
      { kind: 'image', label: 'image', source: { kind: 'unknown' } },
      { kind: 'image', label: 'image', source: { kind: 'unknown' } },
    ])
  })

  it('no prompt and no images extracts nothing', () => {
    expect(extractOllamaRequest(detail('ollama-generate', body(JSON.stringify({})), null))).toBeNull()
  })
})

describe('extractOllamaResponse — ollama-generate', () => {
  it('renders response text with no thinking block when thinking is absent (unchanged behavior)', () => {
    const view = extractOllamaResponse(detail('ollama-generate', null, body(JSON.stringify({ response: 'four' }))))
    expect(view?.messages).toEqual([{ role: 'assistant', blocks: [{ kind: 'markdown', text: 'four' }] }])
  })

  it('R09 — top-level thinking renders as a collapsed thinking block before the response text', () => {
    const view = extractOllamaResponse(
      detail('ollama-generate', null, body(JSON.stringify({ thinking: 'reasoning about it', response: 'four' }))),
    )
    expect(view?.messages).toEqual([
      {
        role: 'assistant',
        blocks: [
          { kind: 'thinking', text: 'reasoning about it' },
          { kind: 'markdown', text: 'four' },
        ],
      },
    ])
  })
})
