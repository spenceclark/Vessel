import type { RequestDetail } from '@/api/types'
import type { RenderBlock, RenderedView, RenderMessage } from './types'

const METRIC_KEYS = [
  'done_reason',
  'total_duration',
  'load_duration',
  'prompt_eval_count',
  'prompt_eval_duration',
  'eval_count',
  'eval_duration',
]

/**
 * D4 — `ollama-chat`/`ollama-generate`. Request `messages`/`prompt` on the request side;
 * response `message`/`response` string plus the final-object metrics (exact token/timing
 * figures, incl. `load_duration` cold-load evidence) as params on the response side.
 */
export function extractOllamaRequest(detail: RequestDetail): RenderedView | null {
  try {
    const req = detail.requestBody?.text ? JSON.parse(detail.requestBody.text) : null
    if (!req) return null

    const messages: RenderMessage[] = []
    if (detail.format === 'ollama-generate') {
      if (typeof req.prompt === 'string' && req.prompt) {
        messages.push({ role: 'user', blocks: [{ kind: 'markdown', text: req.prompt }] })
      }
    } else {
      for (const m of Array.isArray(req.messages) ? req.messages : []) {
        messages.push(requestMessage(m))
      }
    }

    if (messages.length === 0) return null
    return { messages, params: [] }
  } catch {
    return null
  }
}

export function extractOllamaResponse(detail: RequestDetail): RenderedView | null {
  try {
    const resp = detail.responseBody?.text ? JSON.parse(detail.responseBody.text) : null
    if (!resp) return null

    const blocks: RenderBlock[] = []
    if (detail.format === 'ollama-generate') {
      if (typeof resp.response === 'string' && resp.response) blocks.push({ kind: 'markdown', text: resp.response })
    } else {
      const msg = resp.message
      if (typeof msg?.content === 'string' && msg.content) blocks.push({ kind: 'markdown', text: msg.content })
      appendToolCalls(blocks, msg?.tool_calls)
    }

    const params: { k: string; v: string }[] = []
    for (const key of METRIC_KEYS) {
      if (resp[key] !== undefined && resp[key] !== null) params.push({ k: key, v: String(resp[key]) })
    }

    if (blocks.length === 0 && params.length === 0) return null
    return { messages: blocks.length > 0 ? [{ role: 'assistant', blocks }] : [], params }
  } catch {
    return null
  }
}

function requestMessage(m: any): RenderMessage {
  const blocks: RenderBlock[] = []
  if (typeof m?.content === 'string' && m.content) blocks.push({ kind: 'markdown', text: m.content })
  appendToolCalls(blocks, m?.tool_calls)
  return { role: m?.role ?? 'user', blocks }
}

function appendToolCalls(blocks: RenderBlock[], toolCalls: unknown) {
  if (!Array.isArray(toolCalls)) return
  for (const tc of toolCalls) {
    blocks.push({
      kind: 'toolUse',
      name: tc?.function?.name ?? 'tool',
      argsJson: JSON.stringify(tc?.function?.arguments ?? {}, null, 2),
    })
  }
}
