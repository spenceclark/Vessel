import type { BodyPayload, RequestDetail } from '@/api/types'
import type { Decision, DecisionAnswer, DecisionBar, DecisionText, RenderedView } from './types'

/**
 * #113 — `typesafe-systemone` (TypeSafe System One / Jev; OpenRouter "decisions"). A `state`
 * plus a map of typed `questions` in, a map of typed `answers` out under the same keys — no
 * messages, so both extractors fill `RenderedView.decisions` and leave `messages` empty.
 * Nothing is dropped: an answer with an unknown type or no matching question keeps its JSON.
 */
export function extractTypeSafeRequest(detail: RequestDetail): RenderedView | null {
  try {
    const req = parseBody(detail.requestBody)
    const state = decisionText(req?.state)
    const items = questionItems(req)
    if (!state && items.length === 0) return null
    return { messages: [], params: [], decisions: { state, items } }
  } catch {
    return null
  }
}

/** Joins each answer to its question (request order first) so one card can show both. */
export function extractTypeSafeResponse(detail: RequestDetail): RenderedView | null {
  try {
    const answers = parseBody(detail.responseBody)?.answers
    if (!isRecord(answers)) return null

    const items: Decision[] = questionItems(parseBody(detail.requestBody)).map((question) => {
      if (!(question.key in answers)) return question
      const answer = answerOf(question, answers[question.key])
      return answer ? { ...question, answer } : { ...question, rawJson: JSON.stringify(answers[question.key]) }
    })

    for (const [key, answer] of Object.entries(answers)) {
      if (items.some((item) => item.key === key)) continue
      const type = isRecord(answer) && typeof answer.type === 'string' ? answer.type : 'unknown'
      items.push({ key, type, criteria: [], rawJson: JSON.stringify(answer) })
    }

    return items.length === 0 ? null : { messages: [], params: [], decisions: { items } }
  } catch {
    return null
  }
}

/**
 * Display-only extras read from the bodies at render time: the alias the client asked for
 * when the response names a different build (aliases move between releases), and
 * OpenRouter's `usage.cost` / `id` / `provider`.
 */
export function typeSafeMetrics(detail: RequestDetail): { k: string; v: string }[] {
  try {
    const req = parseBody(detail.requestBody)
    const resp = parseBody(detail.responseBody)
    const usage = isRecord(resp?.usage) ? resp.usage : undefined
    const metrics: { k: string; v: string }[] = []

    if (typeof req?.model === 'string' && typeof resp?.model === 'string' && req.model !== resp.model) {
      // The resolved build is already the row's Model; this adds the alias beside it.
      metrics.push({ k: 'Requested model', v: req.model })
    }
    if (typeof usage?.cost === 'number') metrics.push({ k: 'Cost', v: `$${usage.cost}` })
    if (typeof resp?.provider === 'string') metrics.push({ k: 'Provider', v: resp.provider })
    if (typeof resp?.id === 'string') metrics.push({ k: 'Generation id', v: resp.id })
    return metrics
  } catch {
    return []
  }
}

function questionItems(req: Record<string, unknown> | null): Decision[] {
  if (!isRecord(req?.questions)) return []
  return Object.entries(req.questions).map(([key, question]) => {
    const q = isRecord(question) ? question : {}
    return {
      key,
      type: typeof q.type === 'string' ? q.type : 'unknown',
      instructions: decisionText(q.instructions),
      criteria: criteriaOf(q.criteria),
    }
  })
}

/** noul/choice criteria are label → rubric maps (a rubric may be null); score criteria are an ordered array of levels. */
function criteriaOf(criteria: unknown): Decision['criteria'] {
  if (Array.isArray(criteria)) {
    return criteria.map((level, i) => ({ label: String(i), text: typeof level === 'string' ? level : JSON.stringify(level) }))
  }
  if (!isRecord(criteria)) return []
  return Object.entries(criteria).map(([label, rubric]) => ({ label, text: typeof rubric === 'string' ? rubric : undefined }))
}

function answerOf(question: Decision, answer: unknown): DecisionAnswer | undefined {
  if (!isRecord(answer)) return undefined
  const confidence = typeof answer.confidence === 'number' ? answer.confidence : undefined
  const note = (label: string) => question.criteria.find((c) => c.label === label)?.text

  switch (answer.type) {
    case 'noul':
      return typeof answer.noul === 'number'
        ? { value: String(answer.noul), bars: [{ label: 'true', probability: answer.noul }] }
        : undefined

    case 'choice': {
      if (typeof answer.choice !== 'string') return undefined
      const bars = probabilityBars(answer.probabilities)
        .map((bar): DecisionBar => ({ ...bar, note: note(bar.label), chosen: bar.label === answer.choice }))
        .sort((a, b) => b.probability - a.probability)
      return { value: answer.choice, confidence, bars }
    }

    case 'score': {
      if (typeof answer.score !== 'number') return undefined
      const legend = isRecord(answer.legend) ? answer.legend : {}
      const bars = probabilityBars(answer.probabilities)
        .map((bar): DecisionBar => {
          const described = legend[bar.label]
          return { ...bar, note: typeof described === 'string' ? described : note(bar.label) }
        })
        .sort((a, b) => Number(a.label) - Number(b.label))
      const levels = Math.max(bars.length, question.criteria.length)
      return { value: String(answer.score), confidence, bars, scale: levels > 1 ? { value: answer.score, max: levels - 1 } : undefined }
    }

    default:
      return undefined
  }
}

function probabilityBars(probabilities: unknown): DecisionBar[] {
  if (!isRecord(probabilities)) return []
  return Object.entries(probabilities).flatMap(([label, p]) => (typeof p === 'number' ? [{ label, probability: p }] : []))
}

function decisionText(value: unknown): DecisionText | undefined {
  if (value === undefined || value === null || value === '') return undefined
  return typeof value === 'string' ? { text: value, json: false } : { text: JSON.stringify(value), json: true }
}

function parseBody(body: BodyPayload | null): Record<string, unknown> | null {
  const parsed: unknown = body?.text ? JSON.parse(body.text) : null
  return isRecord(parsed) ? parsed : null
}

function isRecord(v: unknown): v is Record<string, unknown> {
  return typeof v === 'object' && v !== null && !Array.isArray(v)
}
