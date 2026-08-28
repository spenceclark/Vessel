import type { RequestDetail } from '@/api/types'
import type { RenderedView } from './types'
import { extractOpenAiChatRequest, extractOpenAiChatResponse } from './openai'
import { extractOpenAiResponsesRequest, extractOpenAiResponsesResponse } from './openaiResponses'
import { extractAnthropicRequest, extractAnthropicResponse } from './anthropic'
import { extractOllamaRequest, extractOllamaResponse } from './ollama'

export type { RenderBlock, RenderedView, RenderMessage } from './types'

/** D4 — dispatches by `detail.format`; `raw` and any extraction failure return null (caller falls back to PrettyJson). */
export function renderRequest(detail: RequestDetail): RenderedView | null {
  switch (detail.format) {
    case 'openai-chat':
      return extractOpenAiChatRequest(detail)
    case 'openai-responses':
      return extractOpenAiResponsesRequest(detail)
    case 'anthropic-messages':
      return extractAnthropicRequest(detail)
    case 'ollama-chat':
    case 'ollama-generate':
      return extractOllamaRequest(detail)
    default:
      return null
  }
}

export function renderResponse(detail: RequestDetail): RenderedView | null {
  switch (detail.format) {
    case 'openai-chat':
      return extractOpenAiChatResponse(detail)
    case 'openai-responses':
      return extractOpenAiResponsesResponse(detail)
    case 'anthropic-messages':
      return extractAnthropicResponse(detail)
    case 'ollama-chat':
    case 'ollama-generate':
      return extractOllamaResponse(detail)
    default:
      return null
  }
}
