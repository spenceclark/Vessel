import type { RequestDetail, StatusBackend } from '@/api/types'

/**
 * Phase 5's curl export intentionally targets Vessel so a pasted command is captured just as
 * any other client request would be. Credentials are placeholders, never captured values.
 */
export function buildCurl(detail: RequestDetail, listen: string, backend?: StatusBackend): string {
  const base = listen.startsWith('http://') || listen.startsWith('https://') ? listen : `http://${listen}`
  const url = `${base}/b/${encodeURIComponent(detail.backend)}${detail.path}`
  const contentType = header(detail.requestHeaders, 'content-type')
  const lines = [`curl -X ${shellQuote(detail.method)} ${shellQuote(url)}`]
  if (contentType) lines.push(`  -H ${shellQuote(`Content-Type: ${contentType}`)}`)

  const backendType = backend?.type.toLowerCase()
  const authEnv = backend?.authEnv
    ?? (backendType === 'anthropic' ? 'ANTHROPIC_API_KEY' : 'OPENAI_API_KEY')
  const needsAuth = backend !== undefined && (
    backend.authEnv !== undefined
    || ((backendType === 'anthropic' || backendType === 'openai' || backendType === 'auto')
      && !isLoopback(backend.baseUrl))
  )
  if (needsAuth && backendType === 'anthropic') {
    lines.push(`  -H "x-api-key: $${authEnv}"`)
    lines.push(`  -H ${shellQuote(`anthropic-version: ${header(detail.requestHeaders, 'anthropic-version') ?? '2023-06-01'}`)}`)
  } else if (needsAuth) {
    lines.push(`  -H "Authorization: Bearer $${authEnv}"`)
  }

  if (!detail.requestBody) return lines.join(' \\\n')

  if (detail.requestBody.base64 !== undefined) {
    return `# Binary request body is base64 encoded; this requires a POSIX base64 utility.\nprintf %s ${shellQuote(detail.requestBody.base64)} | base64 --decode | ${lines.join(' \\\n')} \\\n  --data-binary @-`
  }

  const body = detail.requestBody.text ?? ''
  const marker = heredocMarker(body)
  return `${lines.join(' \\\n')} \\\n  --data-binary @- <<'${marker}'\n${body}\n${marker}`
}

function header(headers: Record<string, string[]> | null, name: string): string | undefined {
  if (!headers) return undefined
  return Object.entries(headers).find(([key]) => key.toLowerCase() === name)?.[1]?.[0]
}

function isLoopback(baseUrl: string): boolean {
  try {
    const host = new URL(baseUrl).hostname
    if (host === 'localhost' || host === '::1' || host === '[::1]') return true
    const octets = host.split('.').map(Number)
    return octets.length === 4
      && octets[0] === 127
      && octets.every((octet) => Number.isInteger(octet) && octet >= 0 && octet <= 255)
  } catch {
    return false
  }
}

function shellQuote(value: string): string {
  return `'${value.replaceAll("'", "'\"'\"'")}'`
}

function heredocMarker(body: string): string {
  let n = 0
  while (true) {
    const marker = n === 0 ? 'VESSEL_BODY' : `VESSEL_BODY_${n}`
    if (!body.split(/\r?\n/).includes(marker)) return marker
    n++
  }
}
