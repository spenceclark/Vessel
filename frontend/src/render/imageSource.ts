import type { ImageSource } from './types'

/**
 * R18 — one place per wire shape that turns captured image data into an `ImageSource`,
 * so every extractor retains it the same way instead of discarding it (as `openai.ts`/
 * `anthropic.ts` did) or never representing it at all (as `ollama.ts` did for `images`).
 * Building an actual `data:` URI here (not just tagging "this was base64") is what lets
 * `MessageView`'s preview render the bytes with zero decoding logic of its own.
 */

/** OpenAI `content[].image_url.url` — either already a `data:` URI or a plain URL. */
export function openAiImageSource(url: unknown): ImageSource {
  if (typeof url !== 'string' || url.length === 0) return { kind: 'unknown' }
  return url.startsWith('data:') ? { kind: 'embedded', dataUri: url } : { kind: 'url', url }
}

/** Anthropic `content[].source` — `{type:"base64", media_type, data}` or `{type:"url", url}`. */
export function anthropicImageSource(source: unknown): ImageSource {
  if (typeof source !== 'object' || source === null) return { kind: 'unknown' }
  const s = source as Record<string, unknown>

  if (s.type === 'base64' && typeof s.media_type === 'string' && typeof s.data === 'string') {
    return { kind: 'embedded', dataUri: `data:${s.media_type};base64,${s.data}` }
  }

  if (s.type === 'url' && typeof s.url === 'string') {
    return { kind: 'url', url: s.url }
  }

  return { kind: 'unknown' }
}

/** Ollama `images[]` — raw base64 strings with no mime type; Ollama's own convention is PNG. */
export function ollamaImageSource(base64: unknown): ImageSource {
  if (typeof base64 !== 'string' || base64.length === 0) return { kind: 'unknown' }
  return { kind: 'embedded', dataUri: `data:image/png;base64,${base64}` }
}
