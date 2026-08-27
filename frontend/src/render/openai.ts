import type { RequestDetail } from '@/api/types'
import type { RenderBlock, RenderedView, RenderMessage } from './types'

/**
 * D4 — `openai-chat`. Request `messages[]` (incl. `tool` role → toolResult), `tools`
 * summarized as a params entry; response from `choices[0].message`. `tool_calls[].function
 * .arguments` stays a string (OpenAI's own wire convention — not re-parsed here). Split
 * request/response to match DetailPane's separate tabs, each keeping its own raw toggle.
 */
export function extractOpenAiChatRequest(detail: RequestDetail): RenderedView | null {
  try {
    const req = detail.requestBody?.text ? JSON.parse(detail.requestBody.text) : null
    if (!req) return null

    const messages: RenderMessage[] = (Array.isArray(req.messages) ? req.messages : []).map(requestMessage)
    const params: { k: string; v: string }[] = []
    if (Array.isArray(req.tools) && req.tools.length > 0) {
      params.push({ k: 'tools', v: JSON.stringify(req.tools, null, 2) })
    }

    if (messages.length === 0 && params.length === 0) return null
    return { messages, params }
  } catch {
    return null
  }
}

export function extractOpenAiChatResponse(detail: RequestDetail): RenderedView | null {
  try {
    const resp = detail.responseBody?.text ? JSON.parse(detail.responseBody.text) : null
    const message = resp?.choices?.[0]?.message
    if (!message) return null
    return { messages: [assistantMessageBlocks(message)], params: [] }
  } catch {
    return null
  }
}

function requestMessage(m: any): RenderMessage {
  const blocks: RenderBlock[] = []

  if (m?.role === 'tool') {
    blocks.push({
      kind: 'toolResult',
      forId: m.tool_call_id,
      content: typeof m.content === 'string' ? m.content : JSON.stringify(m.content ?? ''),
    })
  } else if (typeof m?.content === 'string') {
    if (m.content) blocks.push({ kind: 'markdown', text: m.content })
  } else if (Array.isArray(m?.content)) {
    for (const part of m.content) {
      if (part?.type === 'text') blocks.push({ kind: 'markdown', text: part.text ?? '' })
      else if (part?.type === 'image_url') blocks.push({ kind: 'image', label: 'image' })
      else blocks.push({ kind: 'text', text: JSON.stringify(part) })
    }
  }

  appendToolCalls(blocks, m?.tool_calls)
  return { role: m?.role ?? 'user', blocks }
}

function assistantMessageBlocks(msg: any): RenderMessage {
  const blocks: RenderBlock[] = []
  if (msg.reasoning_content) blocks.push({ kind: 'thinking', text: msg.reasoning_content })
  if (msg.content) blocks.push({ kind: 'markdown', text: msg.content })
  appendToolCalls(blocks, msg.tool_calls)
  return { role: 'assistant', blocks }
}

function appendToolCalls(blocks: RenderBlock[], toolCalls: unknown) {
  if (!Array.isArray(toolCalls)) return
  for (const tc of toolCalls) {
    blocks.push({
      kind: 'toolUse',
      id: tc?.id,
      name: tc?.function?.name ?? 'tool',
      argsJson: tc?.function?.arguments ?? '',
    })
  }
}
