import { describe, expect, it } from 'vitest'
import { renderRequest, renderResponse } from './index'
import { typeSafeMetrics } from './typesafe'
import type { RequestDetail } from '@/api/types'

/** #113 — `typesafe-systemone`. Bodies are capture #5283 (OpenRouter) and the documented direct shape. */

const OPENROUTER_REQUEST = {
  state: 'You have charged me twice and my account is now overdrawn. I need this reversed today.',
  model: '~typesafe/jev-latest',
  questions: {
    urgent: { type: 'noul', instructions: { field: 'urgent', question: 'Does this need a reply within the hour?', goal: 'Triage a support ticket.' } },
    area: {
      type: 'choice',
      instructions: { field: 'area', question: 'Which team owns it?', goal: 'Triage a support ticket.' },
      criteria: { billing: null, bug: null, account: null, other: null },
    },
  },
}

const OPENROUTER_RESPONSE = {
  model: 'typesafe/jev-1.13-20260917',
  answers: {
    urgent: { type: 'noul', noul: 0.79 },
    area: { type: 'choice', choice: 'billing', probabilities: { billing: 1, account: 0, bug: 0, other: 0 }, confidence: 1 },
  },
  usage: { input_tokens: 389, output_tokens: 61, cost: 0.000016338 },
  id: 'gen-dec-1789742825-7iBkOCplJBFo5eSdqSJJ',
  provider: 'TypeSafe',
}

const DIRECT_REQUEST = {
  state: { ticket: 'My app keeps crashing on login.' },
  model: 'jev-1.13.0',
  questions: {
    is_urgent: { type: 'noul', instructions: 'Is this ticket urgent?', criteria: { true: 'Needs a reply within the hour', false: 'Can wait a day' } },
    department: { type: 'choice', instructions: 'Which department?', criteria: { billing: 'Payments', technical: 'Bugs and outages', sales: null } },
    frustration: { type: 'score', instructions: 'How frustrated?', criteria: ['Calm', 'Frustrated', 'Very angry'] },
  },
}

const DIRECT_RESPONSE = {
  model: 'jev-1.13.0',
  answers: {
    is_urgent: { type: 'noul', noul: 0.92 },
    department: { type: 'choice', choice: 'technical', probabilities: { billing: 0.08, technical: 0.85, sales: 0.07 }, confidence: 0.82 },
    frustration: { type: 'score', score: 1.6, legend: { 0: 'Calm', 1: 'Frustrated', 2: 'Very angry' }, probabilities: { 0: 0.05, 1: 0.3, 2: 0.65 }, confidence: 0.78 },
  },
  usage: { input_tokens: 312, output_tokens: 48 },
}

function detail(request: unknown, response: unknown): RequestDetail {
  return {
    format: 'typesafe-systemone',
    requestBody: request === null ? null : { text: JSON.stringify(request) },
    responseBody: response === null ? null : { text: JSON.stringify(response) },
  } as RequestDetail
}

describe('typesafe-systemone request', () => {
  it('renders the state and one item per question, without answers', () => {
    const decisions = renderRequest(detail(OPENROUTER_REQUEST, null))?.decisions
    expect(decisions?.state).toEqual({ text: OPENROUTER_REQUEST.state, json: false })
    expect(decisions?.items.map((i) => [i.key, i.type, i.answer])).toEqual([
      ['urgent', 'noul', undefined],
      ['area', 'choice', undefined],
    ])
    // Object instructions stay JSON for the field list; null rubrics are labels without text.
    expect(decisions?.items[0].instructions).toEqual({ text: JSON.stringify(OPENROUTER_REQUEST.questions.urgent.instructions), json: true })
    expect(decisions?.items[1].criteria).toEqual([{ label: 'billing' }, { label: 'bug' }, { label: 'account' }, { label: 'other' }])
  })

  it('keeps string instructions verbatim, an object state as JSON, and score levels by index', () => {
    const decisions = renderRequest(detail(DIRECT_REQUEST, null))?.decisions
    expect(decisions?.state).toEqual({ text: '{"ticket":"My app keeps crashing on login."}', json: true })
    expect(decisions?.items[0].instructions).toEqual({ text: 'Is this ticket urgent?', json: false })
    expect(decisions?.items[2].criteria).toEqual([
      { label: '0', text: 'Calm' },
      { label: '1', text: 'Frustrated' },
      { label: '2', text: 'Very angry' },
    ])
  })
})

describe('typesafe-systemone response', () => {
  it('joins answers to questions: noul bar, choice bars sorted with rubrics and the winner marked', () => {
    const items = renderResponse(detail(DIRECT_REQUEST, DIRECT_RESPONSE))?.decisions?.items ?? []
    expect(items[0].answer).toEqual({ value: '0.92', bars: [{ label: 'true', probability: 0.92 }] })
    expect(items[1].answer).toEqual({
      value: 'technical',
      confidence: 0.82,
      bars: [
        { label: 'technical', probability: 0.85, note: 'Bugs and outages', chosen: true },
        { label: 'billing', probability: 0.08, note: 'Payments', chosen: false },
        { label: 'sales', probability: 0.07, note: undefined, chosen: false },
      ],
    })
  })

  it('labels score levels from the legend, falling back to the request criteria', () => {
    const items = renderResponse(detail(DIRECT_REQUEST, DIRECT_RESPONSE))?.decisions?.items ?? []
    expect(items[2].answer?.scale).toEqual({ value: 1.6, max: 2 })
    expect(items[2].answer?.bars.map((b) => [b.label, b.note])).toEqual([['0', 'Calm'], ['1', 'Frustrated'], ['2', 'Very angry']])

    const { legend: _legend, ...noLegend } = DIRECT_RESPONSE.answers.frustration
    const fallback = renderResponse(detail(DIRECT_REQUEST, { answers: { frustration: noLegend } }))?.decisions?.items ?? []
    expect(fallback[2].answer?.bars.map((b) => b.note)).toEqual(['Calm', 'Frustrated', 'Very angry'])
  })

  it('never drops an answer: unknown types and unmatched keys keep their JSON', () => {
    const response = { answers: { urgent: { type: 'rank', order: ['a'] }, stray: { type: 'noul', noul: 0.5 } } }
    const items = renderResponse(detail(OPENROUTER_REQUEST, response))?.decisions?.items ?? []
    expect(items.map((i) => [i.key, i.answer, i.rawJson])).toEqual([
      ['urgent', undefined, '{"type":"rank","order":["a"]}'],
      ['area', undefined, undefined],
      ['stray', undefined, '{"type":"noul","noul":0.5}'],
    ])
  })

  it('returns null for an error body so the pane falls back to raw JSON', () => {
    expect(renderResponse(detail(OPENROUTER_REQUEST, { error: { message: 'criteria is required' } }))).toBeNull()
    expect(renderResponse(detail(OPENROUTER_REQUEST, null))).toBeNull()
  })
})

describe('typeSafeMetrics', () => {
  it('shows the requested alias when it resolved to a different build, plus OpenRouter extras', () => {
    expect(typeSafeMetrics(detail(OPENROUTER_REQUEST, OPENROUTER_RESPONSE))).toEqual([
      { k: 'Requested model', v: '~typesafe/jev-latest' },
      { k: 'Cost', v: '$0.000016338' },
      { k: 'Provider', v: 'TypeSafe' },
      { k: 'Generation id', v: 'gen-dec-1789742825-7iBkOCplJBFo5eSdqSJJ' },
    ])
  })

  it('is empty for a direct response whose model matches the request', () => {
    expect(typeSafeMetrics(detail(DIRECT_REQUEST, DIRECT_RESPONSE))).toEqual([])
  })
})
