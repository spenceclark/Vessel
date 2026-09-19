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

  // #113 — the decisions view gets the same all-or-nothing check: one defect anywhere rejects the view.
  describe('decisions', () => {
    const answer = { value: 'billing', confidence: 1, scale: { value: 1, max: 2 }, bars: [{ label: 'billing', probability: 1, note: 'Payments', chosen: true }] }
    const item = { key: 'area', type: 'choice', instructions: { text: 'Which team?', json: false }, criteria: [{ label: 'billing', text: 'Payments' }], answer }
    const withDecisions = (decisions: unknown) => ({ messages: [], params: [], decisions }) as unknown as RenderedView

    it('passes through a well-formed decisions view unchanged', () => {
      const view = withDecisions({ state: { text: 'x', json: false }, items: [item, { key: 'odd', type: 'rank', criteria: [], rawJson: '{}' }] })
      expect(sanitizeRenderedView(view)).toBe(view)
    })

    it.each<[string, unknown]>([
      ['items not an array', { items: 'nope' }],
      ['state text not a string', { state: { text: { a: 1 }, json: true }, items: [] }],
      ['non-string key', { items: [{ ...item, key: 7 }] }],
      ['non-string type', { items: [{ ...item, type: { a: 1 } }] }],
      ['instructions missing json flag', { items: [{ ...item, instructions: { text: 'x' } }] }],
      ['criteria label not a string', { items: [{ ...item, criteria: [{ label: 1 }] }] }],
      ['criteria text not a string', { items: [{ ...item, criteria: [{ label: 'a', text: {} }] }] }],
      ['non-string rawJson', { items: [{ ...item, rawJson: { a: 1 } }] }],
      ['non-string answer value', { items: [{ ...item, answer: { ...answer, value: 0.9 } }] }],
      ['non-number confidence', { items: [{ ...item, answer: { ...answer, confidence: 'high' } }] }],
      ['malformed scale', { items: [{ ...item, answer: { ...answer, scale: { value: '1', max: 2 } } }] }],
      ['bar missing probability', { items: [{ ...item, answer: { ...answer, bars: [{ label: 'billing' }] } }] }],
      ['bar note not a string', { items: [{ ...item, answer: { ...answer, bars: [{ label: 'a', probability: 1, note: {} }] } }] }],
      ['bar chosen not a boolean', { items: [{ ...item, answer: { ...answer, bars: [{ label: 'a', probability: 1, chosen: 'yes' }] } }] }],
    ])('rejects %s', (_name, decisions) => {
      expect(sanitizeRenderedView(withDecisions(decisions))).toBeNull()
    })
  })

  it('null in, null out', () => {
    expect(sanitizeRenderedView(null)).toBeNull()
  })
})
