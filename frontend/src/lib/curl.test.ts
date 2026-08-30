import { execFile } from 'node:child_process'
import { createServer } from 'node:http'
import { promisify } from 'node:util'
import { describe, expect, it } from 'vitest'
import { buildCurl } from './curl'
import type { RequestDetail } from '@/api/types'

function detail(overrides: Partial<RequestDetail> = {}): RequestDetail {
  return {
    id: 4, startedAt: '2026-08-29T12:00:00Z', sessionId: 1, backend: 'openai', tags: [], method: 'POST', path: '/v1/chat/completions', format: 'openai-chat', model: 'm', statusCode: 200, error: null, streamed: false, replayOf: null, durationMs: 10, ttftMs: null, vesselOverheadMs: 1, tokPerSec: null, tokensIn: null, tokensOut: null, tokensCachedRead: null, tokensCachedWrite: null, tokensEstimated: false, stopReason: null, warnings: [], truncated: false, requestHeaders: { 'Content-Type': ['application/json'] }, responseHeaders: null, requestBody: { text: '{"quote":"it\'s safe"}' }, responseBody: null, responseRaw: null,
    ...overrides,
  }
}

describe('buildCurl', () => {
  it('targets Vessel with a heredoc-safe text body and an auth placeholder', () => {
    const command = buildCurl(detail(), 'http://127.0.0.1:4550', {
      name: 'openai', baseUrl: 'https://api.openai.com', type: 'openai', default: false,
      health: { state: 'unknown', lastSeenAt: null },
    })
    expect(command).toContain("'http://127.0.0.1:4550/b/openai/v1/chat/completions'")
    expect(command).toContain('Authorization: Bearer $OPENAI_API_KEY')
    expect(command).toContain("<<'VESSEL_BODY'")
    expect(command).toContain("it's safe")
  })

  it('quotes apostrophes exactly on the method, URL and content type paths', () => {
    const command = buildCurl(detail({
      method: "P'OST",
      path: "/v1/chat/completions?q=it's",
      requestHeaders: { 'Content-Type': ["application/vnd.o'hara+json"] },
    }), '127.0.0.1:4550')

    expect(command).toContain(`-X 'P'"'"'OST'`)
    expect(command).toContain(`'http://127.0.0.1:4550/b/openai/v1/chat/completions?q=it'"'"'s'`)
    expect(command).toContain(`'Content-Type: application/vnd.o'"'"'hara+json'`)
    expect(command).not.toContain(`'\\"'\\"'`)
  })

  it('mirrors replay auth rules for authEnv, loopback and anthropic targets', () => {
    const custom = buildCurl(detail(), '127.0.0.1:4550', backend({ authEnv: 'GEMINI_API_KEY' }))
    expect(custom).toContain('Authorization: Bearer $GEMINI_API_KEY')

    const local = buildCurl(detail(), '127.0.0.1:4550', backend({ baseUrl: 'http://localhost:1234' }))
    expect(local).not.toContain('Authorization:')
    expect(buildCurl(detail(), '127.0.0.1:4550', backend({ baseUrl: 'http://127.0.0.2:1234' }))).not.toContain('Authorization:')
    expect(buildCurl(detail(), '127.0.0.1:4550', backend({ baseUrl: 'http://[::1]:1234' }))).not.toContain('Authorization:')

    const anthropic = buildCurl(detail({ format: 'anthropic-messages', path: '/v1/messages' }), '127.0.0.1:4550', backend({
      type: 'anthropic', baseUrl: 'https://api.anthropic.com', authEnv: 'CLAUDE_KEY',
    }))
    expect(anthropic).toContain('x-api-key: $CLAUDE_KEY')
    expect(anthropic).toContain("'anthropic-version: 2023-06-01'")
    expect(anthropic).not.toContain('Authorization:')
  })

  it('drives the auth scheme off the captured format, not backend.type (#7)', () => {
    // An Anthropic backend left on the default `type: auto` must still emit x-api-key, not
    // an OpenAI bearer token — the request format is the source of truth, backend.type a hint.
    const autoAnthropic = buildCurl(detail({ format: 'anthropic-messages', path: '/v1/messages' }), '127.0.0.1:4550', backend({
      type: 'auto', baseUrl: 'https://api.anthropic.com',
    }))
    expect(autoAnthropic).toContain('x-api-key: $ANTHROPIC_API_KEY')
    expect(autoAnthropic).toContain("'anthropic-version: 2023-06-01'")
    expect(autoAnthropic).not.toContain('Authorization:')

    // backend.type still works as a secondary hint when the format itself isn't Anthropic's
    // (e.g. a raw/passthrough capture against a backend explicitly typed as anthropic).
    const typedAnthropic = buildCurl(detail({ format: 'raw', path: '/v1/messages' }), '127.0.0.1:4550', backend({
      type: 'anthropic', baseUrl: 'https://api.anthropic.com',
    }))
    expect(typedAnthropic).toContain('x-api-key: $ANTHROPIC_API_KEY')

    // An OpenAI-formatted call against an `auto` backend still gets the OpenAI bearer scheme.
    const autoOpenAI = buildCurl(detail({ format: 'openai-chat', path: '/v1/chat/completions' }), '127.0.0.1:4550', backend({
      type: 'auto', baseUrl: 'https://api.openai.com',
    }))
    expect(autoOpenAI).toContain('Authorization: Bearer $OPENAI_API_KEY')
    expect(autoOpenAI).not.toContain('x-api-key:')
  })

  it('targets the browser origin and falls back to 127.0.0.1 for a wildcard host (#8)', () => {
    const dockerOrigin = buildCurl(detail(), 'http://localhost:4550')
    expect(dockerOrigin).toContain("'http://localhost:4550/b/openai/v1/chat/completions'")

    const wildcardV4 = buildCurl(detail(), 'http://0.0.0.0:4550')
    expect(wildcardV4).toContain("'http://127.0.0.1:4550/b/openai/v1/chat/completions'")

    const wildcardV6 = buildCurl(detail(), 'http://[::]:4550')
    expect(wildcardV6).toContain("'http://127.0.0.1:4550/b/openai/v1/chat/completions'")
  })

  it('snapshots every supported request format', () => {
    const cases = [
      detail({ format: 'openai-chat', path: '/v1/chat/completions' }),
      detail({ format: 'openai-responses', path: '/v1/responses' }),
      detail({ format: 'anthropic-messages', path: '/v1/messages' }),
      detail({ format: 'ollama-chat', path: '/api/chat' }),
      detail({ format: 'ollama-generate', path: '/api/generate' }),
      detail({ format: 'raw', path: '/custom' }),
    ].map((value) => buildCurl(value, '127.0.0.1:4550'))

    expect(cases).toMatchInlineSnapshot(`
      [
        "curl -X 'POST' 'http://127.0.0.1:4550/b/openai/v1/chat/completions' \\
        -H 'Content-Type: application/json' \\
        --data-binary @- <<'VESSEL_BODY'
      {"quote":"it's safe"}
      VESSEL_BODY",
        "curl -X 'POST' 'http://127.0.0.1:4550/b/openai/v1/responses' \\
        -H 'Content-Type: application/json' \\
        --data-binary @- <<'VESSEL_BODY'
      {"quote":"it's safe"}
      VESSEL_BODY",
        "curl -X 'POST' 'http://127.0.0.1:4550/b/openai/v1/messages' \\
        -H 'Content-Type: application/json' \\
        --data-binary @- <<'VESSEL_BODY'
      {"quote":"it's safe"}
      VESSEL_BODY",
        "curl -X 'POST' 'http://127.0.0.1:4550/b/openai/api/chat' \\
        -H 'Content-Type: application/json' \\
        --data-binary @- <<'VESSEL_BODY'
      {"quote":"it's safe"}
      VESSEL_BODY",
        "curl -X 'POST' 'http://127.0.0.1:4550/b/openai/api/generate' \\
        -H 'Content-Type: application/json' \\
        --data-binary @- <<'VESSEL_BODY'
      {"quote":"it's safe"}
      VESSEL_BODY",
        "curl -X 'POST' 'http://127.0.0.1:4550/b/openai/custom' \\
        -H 'Content-Type: application/json' \\
        --data-binary @- <<'VESSEL_BODY'
      {"quote":"it's safe"}
      VESSEL_BODY",
      ]
    `)
  })

  it('round-trips the generated command against an HTTP stub', async () => {
    let received: { method?: string; url?: string; contentType?: string; body?: string } = {}
    const server = createServer((request, response) => {
      const chunks: Buffer[] = []
      request.on('data', (chunk: Buffer) => chunks.push(chunk))
      request.on('end', () => {
        received = {
          method: request.method,
          url: request.url,
          contentType: request.headers['content-type'],
          body: Buffer.concat(chunks).toString('utf8'),
        }
        response.end('{}')
      })
    })
    await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve))
    try {
      const address = server.address()
      if (address === null || typeof address === 'string') throw new Error('stub did not bind to TCP')
      const command = buildCurl(detail({ backend: 'stub' }), `127.0.0.1:${address.port}`)
      const shell = process.platform === 'win32' ? 'C:\\Program Files\\Git\\bin\\bash.exe' : '/bin/sh'
      await promisify(execFile)(shell, ['-c', command])
    } finally {
      await new Promise<void>((resolve, reject) => server.close((error) => error ? reject(error) : resolve()))
    }

    expect(received).toEqual({
      method: 'POST',
      url: '/b/stub/v1/chat/completions',
      contentType: 'application/json',
      body: '{"quote":"it\'s safe"}\n',
    })
  })

  it('uses a distinct marker and base64 pipeline for binary bodies', () => {
    const marker = buildCurl(detail({ requestBody: { text: 'VESSEL_BODY\nbody' } }), '127.0.0.1:4550')
    expect(marker).toContain("<<'VESSEL_BODY_1'")
    const binary = buildCurl(detail({ requestBody: { base64: 'AAEC' } }), '127.0.0.1:4550')
    expect(binary).toContain('base64 --decode')
    expect(binary).toContain('--data-binary @-')
  })
})

function backend(overrides: Record<string, unknown> = {}) {
  return {
    name: 'openai', baseUrl: 'https://api.openai.com', type: 'openai', default: false,
    health: { state: 'unknown' as const, lastSeenAt: null },
    ...overrides,
  }
}
