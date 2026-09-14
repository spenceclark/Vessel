import { describe, expect, it } from 'vitest'
import { extractOpenAiChatRequest, extractOpenAiChatResponse } from './openai'
import type { RequestDetail, BodyPayload } from '@/api/types'

/**
 * #79 — Ollama's OpenAI-compatible endpoint puts reasoning text under `reasoning`
 * instead of the `reasoning_content` key vLLM/SGLang use; the extractor must accept either.
 */

function detail(responseBody: BodyPayload | null): RequestDetail {
  return {
    id: 1,
    startedAt: '2026-09-13T00:00:00Z',
    sessionId: 1,
    backend: 'ollama',
    tags: [],
    method: 'POST',
    path: '/v1/chat/completions',
    format: 'openai-chat',
    model: 'qwen3.5',
    statusCode: 200,
    error: null,
    streamed: false,
    replayOf: null,
    replayGroup: null,
    replayPatch: null,
    score: null,
    durationMs: 100,
    ttftMs: null,
    vesselOverheadMs: null,
    tokPerSec: null,
    tokensIn: null,
    tokensOut: null,
    tokensCachedRead: null,
    tokensCachedWrite: null,
    tokensEstimated: false,
    stopReason: 'tool_calls',
    warnings: [],
    truncated: false,
    requestHeaders: null,
    responseHeaders: null,
    requestBody: null,
    responseBody,
    responseRaw: null,
  }
}

function body(text: string): BodyPayload {
  return { text }
}

describe('extractOpenAiChatResponse', () => {
  it('renders a thinking block from reasoning_content (vLLM/SGLang key)', () => {
    const view = extractOpenAiChatResponse(
      detail(body(JSON.stringify({ choices: [{ message: { role: 'assistant', content: 'hi', reasoning_content: 'thinking...' } }] }))),
    )
    expect(view?.messages[0].blocks).toEqual([
      { kind: 'thinking', text: 'thinking...' },
      { kind: 'markdown', text: 'hi' },
    ])
  })

  it('#79 — renders a thinking block from reasoning (Ollama key) alongside empty content and a tool call', () => {
    const view = extractOpenAiChatResponse(
      detail(
        body(
          JSON.stringify({
            choices: [
              {
                message: {
                  role: 'assistant',
                  content: '',
                  reasoning: 'thinking...',
                  tool_calls: [{ id: 't1', type: 'function', function: { name: 'final_result', arguments: '{}' } }],
                },
              },
            ],
          }),
        ),
      ),
    )
    expect(view?.messages[0].blocks).toEqual([
      { kind: 'thinking', text: 'thinking...' },
      { kind: 'toolUse', id: 't1', name: 'final_result', argsJson: '{}' },
    ])
  })
})

describe('extractOpenAiChatRequest', () => {
  it('#81 — declared tools populate `tools`, not a params entry', () => {
    const request = {
      messages: [{ role: 'user', content: 'weather?' }],
      tools: [{ type: 'function', function: { name: 'get_weather', parameters: { type: 'object', properties: { city: { type: 'string' } } } } }],
    }
    const view = extractOpenAiChatRequest({ ...detail(null), requestBody: body(JSON.stringify(request)) })
    expect(view?.tools?.map((t) => t.name)).toEqual(['get_weather'])
    expect(view?.tools?.[0].params).toEqual([{ name: 'city', type: 'string', required: false }])
    expect(view?.params).toEqual([])
  })
})
