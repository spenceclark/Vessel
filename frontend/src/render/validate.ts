import type { RenderBlock, RenderedView, RenderMessage } from './types'

/**
 * R17 — every extractor casts arbitrary captured JSON into this typed view with no
 * runtime check: a captured `messages: [{"role":{"unexpected":"object"}, ...}]` produced
 * a `RenderMessage` whose `role` field held an *object*, which reached React as a child
 * (`<div>{message.role}</div>` in `MessageView`) and threw, blanking the whole app with
 * no error boundary to catch it. TypeScript's `role: string` only ever promised that
 * *if* the JSON matched, so it never protected anything here — the boundary between
 * untrusted captured data and the typed view model needed an actual runtime check.
 *
 * The check is deliberately all-or-nothing: any field that doesn't match its expected
 * shape rejects the whole view (→ `null`, same contract as an extraction failure), rather
 * than trying to partially repair it. A view with one bad message and the rest fine could
 * still be rendered field-by-field, but "coerce what's safe and hope the rest holds up"
 * is exactly the kind of adapter-specific special-casing this file exists to avoid — a
 * uniformly-applied reject-on-any-defect check is simpler to reason about and just as
 * safe, and the existing PrettyJson fallback already handles "show it as raw JSON
 * instead" for a whole view.
 */
export function sanitizeRenderedView(view: RenderedView | null): RenderedView | null {
  if (view === null) return null

  if (view.system !== undefined && typeof view.system !== 'string') return null
  if (!Array.isArray(view.messages) || !view.messages.every(isValidMessage)) return null
  if (!Array.isArray(view.params) || !view.params.every(isValidParam)) return null

  return view
}

function isValidParam(p: unknown): p is { k: string; v: string } {
  return isRecord(p) && typeof p.k === 'string' && typeof p.v === 'string'
}

function isValidMessage(m: unknown): m is RenderMessage {
  return isRecord(m) && typeof m.role === 'string' && Array.isArray(m.blocks) && m.blocks.every(isValidBlock)
}

function isValidBlock(b: unknown): b is RenderBlock {
  if (!isRecord(b) || typeof b.kind !== 'string') return false

  switch (b.kind) {
    case 'markdown':
    case 'text':
    case 'thinking':
      return typeof b.text === 'string'
    case 'image':
      return typeof b.label === 'string' && isValidImageSource(b.source)
    case 'toolUse':
      return (
        typeof b.name === 'string' &&
        typeof b.argsJson === 'string' &&
        (b.id === undefined || typeof b.id === 'string')
      )
    case 'toolResult':
      return typeof b.content === 'string' && (b.forId === undefined || typeof b.forId === 'string')
    default:
      return false
  }
}

function isValidImageSource(s: unknown): boolean {
  if (!isRecord(s) || typeof s.kind !== 'string') return false
  switch (s.kind) {
    case 'embedded':
      return typeof s.dataUri === 'string'
    case 'url':
      return typeof s.url === 'string'
    case 'unknown':
      return true
    default:
      return false
  }
}

function isRecord(v: unknown): v is Record<string, unknown> {
  return typeof v === 'object' && v !== null
}
