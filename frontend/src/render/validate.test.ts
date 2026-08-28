import { describe, expect, it } from 'vitest'
import { sanitizeRenderedView } from './validate'
import type { RenderedView } from './types'

/**
 * R17 — the review's concrete repro: a captured `messages: [{"role":{"unexpected":
 * "object"}, "content":"hello"}]` reached React as a child and blanked the app. This
 * pins the extraction-boundary check that turns that shape into a clean "extraction
 * failed" (`null`), same as any other unrenderable capture, so the caller's existing
 * PrettyJson fallback handles it — never a crash.
 */
describe('sanitizeRenderedView', () => {
  it('passes through a well-formed view unchanged', () => {
    const view: RenderedView = {
      messages: [{ role: 'user', blocks: [{ kind: 'markdown', text: 'hi' }] }],
      params: [{ k: 'temperature', v: '0.7' }],
    }
    expect(sanitizeRenderedView(view)).toBe(view)
  })

  it('rejects a non-string role (the review repro shape)', () => {
    const view = {
      messages: [{ role: { unexpected: 'object' }, blocks: [{ kind: 'markdown', text: 'hello' }] }],
      params: [],
    } as unknown as RenderedView
    expect(sanitizeRenderedView(view)).toBeNull()
  })

  it('rejects a non-string block text field', () => {
    const view = {
      messages: [{ role: 'user', blocks: [{ kind: 'text', text: 42 }] }],
      params: [],
    } as unknown as RenderedView
    expect(sanitizeRenderedView(view)).toBeNull()
  })

  it('rejects a tool block missing its required string fields', () => {
    const view = {
      messages: [{ role: 'assistant', blocks: [{ kind: 'toolUse', name: 'lookup', argsJson: { not: 'a string' } }] }],
      params: [],
    } as unknown as RenderedView
    expect(sanitizeRenderedView(view)).toBeNull()
  })

  it('rejects an image block with a malformed source', () => {
    const view = {
      messages: [{ role: 'user', blocks: [{ kind: 'image', label: 'x', source: { kind: 'embedded' } }] }],
      params: [],
    } as unknown as RenderedView
    expect(sanitizeRenderedView(view)).toBeNull()
  })

  it('null in, null out', () => {
    expect(sanitizeRenderedView(null)).toBeNull()
  })
})
