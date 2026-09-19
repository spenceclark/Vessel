import type { Decisions, RenderBlock, RenderedView, RenderMessage } from './types'

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
  if (view.decisions !== undefined && !isValidDecisions(view.decisions)) return null

  return view
}

// #113 — same all-or-nothing rule for the System One view: every field `DecisionsView` puts
// on screen is checked here, so nothing but strings and numbers reaches React.
function isValidDecisions(d: unknown): d is Decisions {
  return isRecord(d) && isOptionalDecisionText(d.state) && Array.isArray(d.items) && d.items.every(isValidDecision)
}

function isValidDecision(item: unknown): boolean {
  return (
    isRecord(item) &&
    typeof item.key === 'string' &&
    typeof item.type === 'string' &&
    isOptionalDecisionText(item.instructions) &&
    isArrayOfOptionalStrings(item.criteria, ['text']) &&
    (item.criteria as unknown[]).every((c) => isRecord(c) && typeof c.label === 'string') &&
    (item.rawJson === undefined || typeof item.rawJson === 'string') &&
    (item.answer === undefined || isValidDecisionAnswer(item.answer))
  )
}

function isValidDecisionAnswer(a: unknown): boolean {
  return (
    isRecord(a) &&
    typeof a.value === 'string' &&
    (a.confidence === undefined || typeof a.confidence === 'number') &&
    (a.scale === undefined || (isRecord(a.scale) && typeof a.scale.value === 'number' && typeof a.scale.max === 'number')) &&
    Array.isArray(a.bars) &&
    a.bars.every(
      (b) =>
        isRecord(b) &&
        typeof b.label === 'string' &&
        typeof b.probability === 'number' &&
        (b.note === undefined || typeof b.note === 'string') &&
        (b.chosen === undefined || typeof b.chosen === 'boolean'),
    )
  )
}

function isOptionalDecisionText(t: unknown): boolean {
  return t === undefined || (isRecord(t) && typeof t.text === 'string' && typeof t.json === 'boolean')
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
      return typeof b.text === 'string' && (b.citations === undefined || isArrayOfOptionalStrings(b.citations, ['url', 'title', 'citedText']))
    case 'thinking':
      return typeof b.text === 'string'
    case 'image':
      return typeof b.label === 'string' && isValidImageSource(b.source)
    case 'toolUse':
      return (
        typeof b.name === 'string' &&
        typeof b.argsJson === 'string' &&
        (b.id === undefined || typeof b.id === 'string') &&
        (b.server === undefined || typeof b.server === 'boolean')
      )
    case 'toolResult':
      return typeof b.content === 'string' && (b.forId === undefined || typeof b.forId === 'string')
    case 'serverToolResult':
      return (
        typeof b.toolType === 'string' &&
        typeof b.rawJson === 'string' &&
        (b.forId === undefined || typeof b.forId === 'string') &&
        (b.error === undefined || typeof b.error === 'string') &&
        isArrayOfOptionalStrings(b.items, ['title', 'url', 'meta', 'snippet'])
      )
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

function isArrayOfOptionalStrings(v: unknown, keys: string[]): boolean {
  return Array.isArray(v) && v.every((x) => isRecord(x) && keys.every((k) => x[k] === undefined || typeof x[k] === 'string'))
}

function isRecord(v: unknown): v is Record<string, unknown> {
  return typeof v === 'object' && v !== null
}
