import type { RequestDetail } from '@/api/types'
import { anthropicImageSource } from './imageSource'
import { formatBytes } from '@/lib/format'
import type { Citation, RenderBlock, RenderedView, RenderMessage, ServerToolItem } from './types'
import { extractTools } from './tools'

/**
 * D4 — `anthropic-messages`. `system` + `messages[]` content blocks (`tool_use`,
 * `tool_result`) on the request side; response message blocks incl. `thinking` on the
 * response side. `tool_use.input` is already parsed JSON (the adapter parses it),
 * pretty-printed here for display.
 */
export function extractAnthropicRequest(detail: RequestDetail): RenderedView | null {
  try {
    const req = detail.requestBody?.text ? JSON.parse(detail.requestBody.text) : null
    if (!req) return null

    const system = flattenSystem(req.system)
    const messages: RenderMessage[] = (Array.isArray(req.messages) ? req.messages : [])
      .map((m: any) => toRenderMessage(m?.role ?? 'user', m?.content))

    const tools = extractTools('anthropic-messages', req.tools)

    if (messages.length === 0 && !system && tools.length === 0) return null
    return { system, messages, params: [], tools }
  } catch {
    return null
  }
}

export function extractAnthropicResponse(detail: RequestDetail): RenderedView | null {
  try {
    const resp = detail.responseBody?.text ? JSON.parse(detail.responseBody.text) : null
    if (!Array.isArray(resp?.content)) return null
    return { messages: [toRenderMessage(resp.role ?? 'assistant', resp.content)], params: [] }
  } catch {
    return null
  }
}

function flattenSystem(system: unknown): string | undefined {
  if (typeof system === 'string') return system || undefined
  if (Array.isArray(system)) {
    const text = system.map((b: any) => (typeof b === 'string' ? b : (b?.text ?? ''))).join('\n\n')
    return text || undefined
  }
  return undefined
}

function toRenderMessage(role: string, content: unknown): RenderMessage {
  const blocks: RenderBlock[] = []

  if (typeof content === 'string') {
    if (content) blocks.push({ kind: 'markdown', text: content })
  } else if (Array.isArray(content)) {
    for (const block of content) {
      switch (block?.type) {
        case 'text': {
          const citations = Array.isArray(block.citations) ? block.citations.map(toCitation) : undefined
          blocks.push(citations?.length ? { kind: 'markdown', text: block.text ?? '', citations } : { kind: 'markdown', text: block.text ?? '' })
          break
        }
        case 'thinking':
          blocks.push({ kind: 'thinking', text: block.thinking ?? '' })
          break
        case 'tool_use':
          blocks.push({
            kind: 'toolUse',
            id: block.id,
            name: block.name ?? 'tool',
            argsJson: JSON.stringify(block.input ?? {}, null, 2),
          })
          break
        case 'server_tool_use':
          blocks.push({
            kind: 'toolUse',
            id: block.id,
            name: block.name ?? 'tool',
            argsJson: JSON.stringify(block.input ?? {}, null, 2),
            server: true,
          })
          break
        case 'tool_result': {
          const resultContent = typeof block.content === 'string' ? block.content : JSON.stringify(block.content ?? '')
          blocks.push({ kind: 'toolResult', forId: block.tool_use_id, content: resultContent })
          break
        }
        case 'image':
          blocks.push({ kind: 'image', label: 'image', source: anthropicImageSource(block.source) })
          break
        default:
          if (typeof block?.type === 'string' && block.type.endsWith('_tool_result')) blocks.push(toServerToolResult(block))
          else blocks.push({ kind: 'text', text: JSON.stringify(block) })
      }
    }
  }

  return { role, blocks }
}

function toCitation(c: any): Citation {
  return { url: str(c?.url), title: str(c?.title) ?? str(c?.document_title), citedText: str(c?.cited_text) }
}

const str = (v: unknown) => (typeof v === 'string' ? v : undefined)

const SNIPPET_LENGTH = 500

/**
 * #82 — `web_search_tool_result` / `web_fetch_tool_result` get items; any other server
 * tool result degrades to a card with raw JSON only. `encrypted_*` fields are opaque
 * provider blobs and are dropped everywhere, including the raw JSON.
 */
function toServerToolResult(block: any): RenderBlock {
  const content = block.content
  const items: ServerToolItem[] = []
  let error: string | undefined

  if (typeof content?.type === 'string' && content.type.endsWith('_error')) {
    error = String(content.error_code ?? content.type)
  } else if (block.type === 'web_search_tool_result' && Array.isArray(content)) {
    for (const r of content) {
      if (r?.type === 'web_search_result') items.push({ title: str(r.title), url: str(r.url), meta: str(r.page_age) })
    }
  } else if (block.type === 'web_fetch_tool_result' && content?.type === 'web_fetch_result') {
    const source = content.content?.source
    const snippet =
      source?.type === 'text' && typeof source.data === 'string'
        ? source.data.slice(0, SNIPPET_LENGTH) + (source.data.length > SNIPPET_LENGTH ? '…' : '')
        : source?.type === 'base64' && typeof source.data === 'string'
          ? `${source.media_type ?? 'binary'} · ${formatBytes(Math.floor((source.data.length * 3) / 4))} (not shown)`
          : undefined
    items.push({ title: str(content.content?.title), url: str(content.url), meta: str(content.retrieved_at), snippet })
  }

  return {
    kind: 'serverToolResult',
    forId: str(block.tool_use_id),
    toolType: block.type.slice(0, -'_tool_result'.length),
    error,
    items,
    rawJson: JSON.stringify(block, (k, v) => (k.startsWith('encrypted_') ? undefined : v), 2),
  }
}
