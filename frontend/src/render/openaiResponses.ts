import type { RequestDetail } from '@/api/types'
import { openAiImageSource } from './imageSource'
import type { RenderBlock, RenderedView, RenderMessage } from './types'

/**
 * D4 — `openai-responses`. Structurally different from `openai-chat`: request `input`
 * (a string, or an array of message/tool items) instead of `messages`; response `output[]`
 * (typed items: `message`, `reasoning`, `function_call`, …) instead of `choices`. Each
 * output item becomes its own message card — they're discrete turns/actions in this API,
 * not parts of one assistant message. `instructions` plays the same role as `system`
 * elsewhere. Unrecognized item types (web_search_call, computer_call, …) still render, as
 * raw JSON, rather than silently disappearing.
 */
export function extractOpenAiResponsesRequest(detail: RequestDetail): RenderedView | null {
  try {
    const req = detail.requestBody?.text ? JSON.parse(detail.requestBody.text) : null
    if (!req) return null

    const system = typeof req.instructions === 'string' && req.instructions ? req.instructions : undefined

    const messages: RenderMessage[] = []
    if (typeof req.input === 'string') {
      if (req.input) messages.push({ role: 'user', blocks: [{ kind: 'markdown', text: req.input }] })
    } else if (Array.isArray(req.input)) {
      for (const item of req.input) {
        const message = inputItemMessage(item)
        if (message) messages.push(message)
      }
    }

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

export function extractOpenAiResponsesResponse(detail: RequestDetail): RenderedView | null {
  try {
    const resp = detail.responseBody?.text ? JSON.parse(detail.responseBody.text) : null
    if (!Array.isArray(resp?.output)) return null

    const messages = resp.output.map(outputItemMessage).filter((m: RenderMessage | null): m is RenderMessage => m !== null)
    if (messages.length === 0) return null
    return { messages, params: [] }
  } catch {
    return null
  }
}

function inputItemMessage(item: any): RenderMessage | null {
  switch (item?.type) {
    case 'function_call':
      return { role: 'assistant', blocks: [toolUseBlock(item)] }
    case 'function_call_output':
      return { role: 'tool', blocks: [{ kind: 'toolResult', forId: item?.call_id, content: outputText(item?.output) }] }
    case 'reasoning':
      return { role: 'assistant', blocks: reasoningBlocks(item) }

    // A plain message item omits `type` on the wire.
    case undefined:
    case 'message': {
      const blocks = contentBlocks(item?.content)
      return blocks.length === 0 ? null : { role: item?.role ?? 'user', blocks }
    }

    default:
      return { role: item?.role ?? 'user', blocks: [{ kind: 'text', text: JSON.stringify(item) }] }
  }
}

function outputItemMessage(item: any): RenderMessage | null {
  switch (item?.type) {
    case 'message': {
      const blocks = contentBlocks(item?.content)
      return blocks.length === 0 ? null : { role: item?.role ?? 'assistant', blocks }
    }
    case 'reasoning': {
      const blocks = reasoningBlocks(item)
      return blocks.length === 0 ? null : { role: 'assistant', blocks }
    }
    case 'function_call':
      return { role: 'assistant', blocks: [toolUseBlock(item)] }
    case 'function_call_output':
      return { role: 'tool', blocks: [{ kind: 'toolResult', forId: item?.call_id, content: outputText(item?.output) }] }

    // web_search_call, file_search_call, image_generation_call, computer_call, mcp_call,
    // and any future item type: shown as raw JSON rather than dropped from the view.
    default:
      return { role: item?.type ?? 'assistant', blocks: [{ kind: 'text', text: JSON.stringify(item) }] }
  }
}

function toolUseBlock(item: any): RenderBlock {
  return { kind: 'toolUse', id: item?.call_id, name: item?.name ?? 'tool', argsJson: item?.arguments ?? '' }
}

function reasoningBlocks(item: any): RenderBlock[] {
  const text = Array.isArray(item?.summary)
    ? item.summary.map((s: any) => s?.text ?? '').filter(Boolean).join('\n\n')
    : ''
  return text ? [{ kind: 'thinking', text }] : []
}

function outputText(output: unknown): string {
  return typeof output === 'string' ? output : JSON.stringify(output ?? '')
}

/** Responses API content parts: `input_text`/`output_text` as markdown, `refusal` as text, images as an image block. */
function contentBlocks(content: unknown): RenderBlock[] {
  if (typeof content === 'string') {
    return content ? [{ kind: 'markdown', text: content }] : []
  }

  if (!Array.isArray(content)) return []

  const blocks: RenderBlock[] = []
  for (const part of content) {
    switch (part?.type) {
      case 'input_text':
      case 'output_text':
        if (part.text) blocks.push({ kind: 'markdown', text: part.text })
        break
      case 'refusal':
        if (part.refusal) blocks.push({ kind: 'text', text: part.refusal })
        break
      case 'input_image':
        blocks.push({ kind: 'image', label: 'image', source: openAiImageSource(part.image_url) })
        break
      default:
        blocks.push({ kind: 'text', text: JSON.stringify(part) })
    }
  }

  return blocks
}
