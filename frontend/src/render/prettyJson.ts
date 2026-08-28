/**
 * ui-spec.md §9.1 — structured-output workloads (Responses API `text.format`
 * json-schema, "reply in JSON" prompting) produce a text/markdown block whose entire
 * content is one line of JSON — faithful, but unreadable crammed into a markdown
 * paragraph. Whole-block only: a block is JSON here only if its *entire* trimmed text
 * parses as one JSON object or array — a primitive (`"hello"`, `42`, `true`, `null`)
 * or JSON embedded partway through prose must fall through to ordinary rendering.
 * Presentation only — the underlying block text (and everything derived from it
 * upstream: `response_text`, FTS, the Raw JSON toggle) is untouched.
 */
export function tryPrettyJson(text: string): string | null {
  const trimmed = text.trim()
  if (trimmed.length === 0 || (trimmed[0] !== '{' && trimmed[0] !== '[')) {
    return null
  }

  try {
    const parsed: unknown = JSON.parse(trimmed)
    return typeof parsed === 'object' && parsed !== null ? JSON.stringify(parsed, null, 2) : null
  } catch {
    return null
  }
}
