import type { RequestDetail } from '@/api/types'
import type { RenderBlock, RenderedView, RenderMessage } from './types'

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

    const params: { k: string; v: string }[] = []
    if (Array.isArray(req.tools) && req.tools.length > 0) {
      params.push({ k: 'tools', v: JSON.stringify(req.tools, null, 2) })
    }

    if (messages.length === 0 && !system && params.length === 0) return null
    return { system, messages, params }
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
        case 'text':
          blocks.push({ kind: 'markdown', text: block.text ?? '' })
          break
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
        case 'tool_result': {
          const resultContent = typeof block.content === 'string' ? block.content : JSON.stringify(block.content ?? '')
          blocks.push({ kind: 'toolResult', forId: block.tool_use_id, content: resultContent })
          break
        }
        case 'image':
          blocks.push({ kind: 'image', label: 'image' })
          break
        default:
          blocks.push({ kind: 'text', text: JSON.stringify(block) })
      }
    }
  }

  return { role, blocks }
}
