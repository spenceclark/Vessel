import { describe, expect, it } from 'vitest'
import { extractAnthropicResponse } from './anthropic'
import { renderResponse } from './index'
import type { RequestDetail } from '@/api/types'

/**
 * #82 — Anthropic server-tool blocks (`server_tool_use`, `web_search_tool_result`,
 * `web_fetch_tool_result`, cited `text`) render as typed blocks instead of JSON blobs.
 * Fixture shapes are trimmed from a real capture (request #1972).
 */

function detail(content: unknown[]): RequestDetail {
  return {
    id: 1,
    startedAt: '2026-09-14T00:00:00Z',
    sessionId: 1,
    backend: 'anthropic',
    tags: [],
    method: 'POST',
    path: '/v1/messages',
    format: 'anthropic-messages',
    model: 'claude-haiku-4-5',
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
    stopReason: 'end_turn',
    warnings: [],
    truncated: false,
    requestHeaders: null,
    responseHeaders: null,
    requestBody: null,
    responseBody: { text: JSON.stringify({ role: 'assistant', content }) },
    responseRaw: null,
  }
}

function blocks(content: unknown[]) {
  return extractAnthropicResponse(detail(content))!.messages[0].blocks
}

describe('extractAnthropicResponse — server tools', () => {
  it('server_tool_use becomes a server toolUse', () => {
    expect(blocks([{ type: 'server_tool_use', id: 'srvtoolu_1', name: 'web_search', input: { query: 'vessel' } }])).toEqual([
      { kind: 'toolUse', id: 'srvtoolu_1', name: 'web_search', argsJson: '{\n  "query": "vessel"\n}', server: true },
    ])
  })

  it('web_search_tool_result lists results without encrypted content', () => {
    const [block] = blocks([
      {
        type: 'web_search_tool_result',
        tool_use_id: 'srvtoolu_1',
        content: [
          { type: 'web_search_result', title: 'A', url: 'https://a.example', page_age: '2 days ago', encrypted_content: 'SECRET_A' },
          { type: 'web_search_result', title: 'B', url: 'https://b.example', page_age: null, encrypted_content: 'SECRET_B' },
        ],
      },
    ])

    expect(block).toMatchObject({
      kind: 'serverToolResult',
      forId: 'srvtoolu_1',
      toolType: 'web_search',
      items: [
        { title: 'A', url: 'https://a.example', meta: '2 days ago' },
        { title: 'B', url: 'https://b.example', meta: undefined },
      ],
    })
    expect(JSON.stringify(block)).not.toContain('SECRET')
  })

  it('web_search_tool_result error sets error and no items', () => {
    const [block] = blocks([
      {
        type: 'web_search_tool_result',
        tool_use_id: 'srvtoolu_1',
        content: { type: 'web_search_tool_result_error', error_code: 'max_uses_exceeded' },
      },
    ])
    expect(block).toMatchObject({ kind: 'serverToolResult', toolType: 'web_search', error: 'max_uses_exceeded', items: [] })
  })

  it('web_fetch_tool_result with a text source yields one item with a snippet', () => {
    const data = 'x'.repeat(600)
    const [block] = blocks([
      {
        type: 'web_fetch_tool_result',
        tool_use_id: 'srvtoolu_2',
        content: {
          type: 'web_fetch_result',
          url: 'https://docs.example/page',
          retrieved_at: '2026-09-14T00:00:00Z',
          content: { type: 'document', source: { type: 'text', media_type: 'text/plain', data }, title: 'Docs', citations: { enabled: true } },
        },
      },
    ])

    expect(block).toMatchObject({
      kind: 'serverToolResult',
      toolType: 'web_fetch',
      items: [{ title: 'Docs', url: 'https://docs.example/page', meta: '2026-09-14T00:00:00Z', snippet: 'x'.repeat(500) + '…' }],
    })
  })

  it('an unknown *_tool_result degrades to a generic card with raw JSON', () => {
    const [block] = blocks([
      { type: 'code_execution_tool_result', tool_use_id: 'srvtoolu_3', content: { type: 'code_execution_result', stdout: 'hi', return_code: 0 } },
    ])
    expect(block).toMatchObject({ kind: 'serverToolResult', forId: 'srvtoolu_3', toolType: 'code_execution', items: [] })
    expect(block.kind === 'serverToolResult' && block.rawJson).toContain('"stdout": "hi"')
  })

  it('text with citations keeps the text and collects citations', () => {
    expect(
      blocks([
        {
          type: 'text',
          text: 'Vessel is a proxy.',
          citations: [
            { type: 'web_search_result_location', url: 'https://a.example', title: 'A', cited_text: 'a proxy', encrypted_index: 'SECRET_I' },
          ],
        },
      ]),
    ).toEqual([
      { kind: 'markdown', text: 'Vessel is a proxy.', citations: [{ url: 'https://a.example', title: 'A', citedText: 'a proxy' }] },
    ])
  })

  it('the sanitized view never contains encrypted_content', () => {
    const view = renderResponse(
      detail([
        { type: 'server_tool_use', id: 's1', name: 'web_search', input: { query: 'q' } },
        {
          type: 'web_search_tool_result',
          tool_use_id: 's1',
          content: [{ type: 'web_search_result', title: 'A', url: 'https://a.example', encrypted_content: 'SECRET' }],
        },
      ]),
    )
    expect(view).not.toBeNull()
    expect(JSON.stringify(view)).not.toContain('encrypted_content')
    expect(JSON.stringify(view)).not.toContain('SECRET')
  })
})
