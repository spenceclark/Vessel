import { describe, expect, it } from 'vitest'
import { countToolCalls, extractTools } from './tools'
import { extractAnthropicRequest } from './anthropic'
import { extractOpenAiResponsesRequest } from './openaiResponses'
import type { RequestDetail } from '@/api/types'

/** #81 — declared tool lists normalized into structured defs for the Tools tab. */

describe('extractTools', () => {
  it('openai-chat function tool', () => {
    const [tool] = extractTools('openai-chat', [
      {
        type: 'function',
        function: {
          name: 'get_weather',
          description: 'Weather for a city',
          parameters: { type: 'object', properties: { city: { type: 'string', description: 'City name' } }, required: ['city'] },
        },
      },
    ])
    expect(tool).toMatchObject({
      name: 'get_weather',
      kind: 'function',
      description: 'Weather for a city',
      params: [{ name: 'city', type: 'string', required: true, description: 'City name' }],
    })
    expect(JSON.parse(tool.rawJson).function.name).toBe('get_weather')
  })

  it('responses flat function tool and built-in tool', () => {
    const tools = extractTools('openai-responses', [
      { type: 'function', name: 'lookup', description: 'Look up', parameters: { type: 'object', properties: { q: { type: 'string' } } } },
      { type: 'web_search_preview', search_context_size: 'low' },
    ])
    expect(tools[0]).toMatchObject({ name: 'lookup', kind: 'function', params: [{ name: 'q', type: 'string', required: false }] })
    expect(tools[1]).toMatchObject({
      name: 'web_search_preview',
      kind: 'server',
      serverType: 'web_search_preview',
      params: [],
      config: [{ k: 'search_context_size', v: 'low' }],
    })
  })

  it('anthropic custom tool', () => {
    const [tool] = extractTools('anthropic-messages', [
      { name: 'read_file', description: 'Read a file', input_schema: { type: 'object', properties: { path: { type: 'string' } }, required: ['path'] } },
    ])
    expect(tool).toMatchObject({ name: 'read_file', kind: 'function', params: [{ name: 'path', type: 'string', required: true }] })
  })

  it('anthropic server tools drop null config fields', () => {
    const tools = extractTools('anthropic-messages', [
      { type: 'web_search_20250305', name: 'web_search', max_uses: 5, allowed_domains: null, blocked_domains: null },
      { type: 'web_fetch_20250910', name: 'web_fetch', allowed_domains: ['example.com'] },
    ])
    expect(tools).toEqual([
      expect.objectContaining({ name: 'web_search', kind: 'server', serverType: 'web_search_20250305', params: [], config: [{ k: 'max_uses', v: '5' }] }),
      expect.objectContaining({ name: 'web_fetch', kind: 'server', config: [{ k: 'allowed_domains', v: '["example.com"]' }] }),
    ])
  })

  it('anyOf [string, null] and type arrays join with |', () => {
    const [tool] = extractTools('openai-chat', [
      {
        type: 'function',
        function: {
          name: 't',
          parameters: { properties: { a: { anyOf: [{ type: 'string' }, { type: 'null' }] }, b: { type: ['integer', 'null'] } } },
        },
      },
    ])
    expect(tool.params.map((p) => p.type)).toEqual(['string | null', 'integer | null'])
  })

  it('enum and default values', () => {
    const [tool] = extractTools('openai-chat', [
      {
        type: 'function',
        function: {
          name: 't',
          parameters: { properties: { unit: { type: 'string', enum: ['c', 'f'], default: 'c' }, mode: { enum: [1, 2] } } },
        },
      },
    ])
    expect(tool.params[0]).toMatchObject({ type: 'string', enumValues: ['c', 'f'], defaultJson: '"c"' })
    expect(tool.params[1]).toMatchObject({ type: 'enum', enumValues: ['1', '2'] })
  })

  it('nested object and array-of-object', () => {
    const [tool] = extractTools('openai-chat', [
      {
        type: 'function',
        function: {
          name: 't',
          parameters: {
            properties: {
              opts: { type: 'object', properties: { depth: { type: 'integer' } }, required: ['depth'] },
              items: { type: 'array', items: { type: 'object', properties: { id: { type: 'string' } } } },
              tags: { type: 'array', items: { type: 'string' } },
            },
          },
        },
      },
    ])
    expect(tool.params[0]).toMatchObject({ type: 'object', children: [{ name: 'depth', type: 'integer', required: true }] })
    expect(tool.params[1]).toMatchObject({ type: 'array<object>', children: [{ name: 'id', type: 'string' }] })
    expect(tool.params[2]).toMatchObject({ type: 'array<string>' })
    expect(tool.params[2].children).toBeUndefined()
  })

  it('caps nesting depth at 4', () => {
    let schema: Record<string, unknown> = { type: 'string' }
    for (let i = 0; i < 6; i++) schema = { type: 'object', properties: { n: schema } }
    const [tool] = extractTools('openai-chat', [{ type: 'function', function: { name: 't', parameters: schema } }])
    let depth = 0
    for (let params = tool.params; params.length > 0; params = params[0].children ?? []) depth++
    expect(depth).toBe(4)
  })

  it('malformed schema does not throw', () => {
    const tools = extractTools('openai-chat', [
      null,
      'nope',
      { type: 'function', function: { name: 42, parameters: { properties: { a: null, b: { type: 7 }, c: { anyOf: 'x' } }, required: 'a' } } },
    ])
    expect(tools).toHaveLength(1)
    expect(tools[0].name).toBe('tool')
    expect(tools[0].params).toEqual([
      { name: 'a', type: 'unknown', required: false },
      expect.objectContaining({ name: 'b', type: 'unknown' }),
      expect.objectContaining({ name: 'c', type: 'unknown' }),
    ])
  })

  it('empty or absent tools → []', () => {
    expect(extractTools('openai-chat', [])).toEqual([])
    expect(extractTools('openai-chat', undefined)).toEqual([])
    expect(extractTools('anthropic-messages', { not: 'an array' })).toEqual([])
  })
})

describe('countToolCalls', () => {
  it('counts toolUse blocks and anthropic server_tool_use text blocks', () => {
    const request = {
      messages: [
        {
          role: 'assistant',
          blocks: [
            { kind: 'toolUse' as const, name: 'read_file', argsJson: '{}' },
            { kind: 'text' as const, text: JSON.stringify({ id: 's1', type: 'server_tool_use', name: 'web_search', input: {} }) },
          ],
        },
      ],
      params: [],
    }
    const response = { messages: [{ role: 'assistant', blocks: [{ kind: 'toolUse' as const, name: 'read_file', argsJson: '{}' }] }], params: [] }
    const counts = countToolCalls([request, response, null])
    expect(counts.get('read_file')).toBe(2)
    expect(counts.get('web_search')).toBe(1)
  })
})

function detail(format: string, request: unknown): RequestDetail {
  return {
    id: 1, startedAt: '2026-09-14T00:00:00Z', sessionId: 1, backend: 'b', tags: [], method: 'POST', path: '/', format,
    model: null, statusCode: 200, error: null, streamed: false, replayOf: null, replayGroup: null, replayPatch: null,
    score: null, durationMs: 1, ttftMs: null, vesselOverheadMs: null, tokPerSec: null, tokensIn: null, tokensOut: null,
    tokensCachedRead: null, tokensCachedWrite: null, tokensEstimated: false, stopReason: null, warnings: [], truncated: false,
    requestHeaders: null, responseHeaders: null, requestBody: { text: JSON.stringify(request) }, responseBody: null, responseRaw: null,
  }
}

describe('renderers populate tools instead of a tools param', () => {
  it('anthropic-messages', () => {
    const view = extractAnthropicRequest(
      detail('anthropic-messages', {
        max_tokens: 10,
        messages: [{ role: 'user', content: 'hi' }],
        tools: [{ name: 'a', input_schema: { type: 'object' } }, { type: 'web_search_20250305', name: 'web_search' }],
      }),
    )
    expect(view?.tools?.map((t) => [t.name, t.kind])).toEqual([['a', 'function'], ['web_search', 'server']])
    expect(view?.params.some((p) => p.k === 'tools')).toBe(false)
  })

  it('openai-responses', () => {
    const view = extractOpenAiResponsesRequest(
      detail('openai-responses', { input: 'hi', tools: [{ type: 'function', name: 'f', parameters: {} }] }),
    )
    expect(view?.tools?.map((t) => t.name)).toEqual(['f'])
    expect(view?.params.some((p) => p.k === 'tools')).toBe(false)
  })
})
