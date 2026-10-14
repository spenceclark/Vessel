import type { RenderedView } from './types'

/**
 * #147 — which earlier request this one continues. The previous turn is the earlier request
 * whose normalised message list is the longest strict prefix of this one's, newest first on
 * ties: same-tag candidates first, then any tag. Longest rather than newest is what keeps a
 * retry of an earlier, shorter turn from claiming messages it never carried. Prefix matching
 * (rather than "the row before") is what keeps this right under interleaved multi-agent
 * traffic. An identical list (an exact retry) is not a previous turn — there has to be
 * something new.
 *
 * OpenAI Chat and Ollama carry the system prompt as leading `system`/`developer` messages
 * rather than in `view.system`; those count as the system prompt, not the conversation, so a
 * changed prompt is reported as changed rather than breaking the prefix. `start` is where
 * the carried-over messages begin in this request's `messages`.
 */
export type PreviousTurn =
  | { kind: 'match'; id: number; start: number; carried: number; systemChanged: boolean; toolsChanged: boolean }
  | { kind: 'rewritten'; id: number }

/**
 * The formats whose request carries the whole conversation. OpenAI Responses is left out: with
 * `previous_response_id` the history is held server-side and the request has only the new
 * input, so every continuation would read as "context rewritten".
 */
export const PREVIOUS_TURN_FORMATS: ReadonlySet<string> = new Set(['openai-chat', 'anthropic-messages', 'ollama-chat'])

export interface TurnCandidate {
  id: number
  tags: string[]
}

/**
 * `candidates` newest first (the list endpoint's order), already scoped to the same session
 * and format. `load` returns a candidate's rendered request, or null when it doesn't render.
 * Candidates load one at a time; a group stops early only on a prefix nothing can beat.
 */
export async function findPreviousTurn(
  current: { tags: string[]; view: RenderedView },
  candidates: TurnCandidate[],
  load: (id: number) => Promise<RenderedView | null>,
): Promise<PreviousTurn | null> {
  const cur = split(current.view)
  if (cur.conversation.length === 0) return null

  const tagKey = sameTags(current.tags)
  const sameTag = candidates.filter((c) => tagKey(c.tags))

  for (const group of [sameTag, candidates.filter((c) => !tagKey(c.tags))]) {
    let best: { id: number; view: RenderedView; prev: ReturnType<typeof split> } | null = null
    for (const candidate of group) {
      const view = await load(candidate.id)
      if (!view) continue
      const prev = split(view)
      // Strictly longer only, so the newest wins a tie (candidates arrive newest first).
      if (!isStrictPrefix(prev.conversation, cur.conversation)) continue
      if (best && prev.conversation.length <= best.prev.conversation.length) continue
      best = { id: candidate.id, view, prev }
      if (prev.conversation.length === cur.conversation.length - 1) break
    }
    if (best) {
      return {
        kind: 'match',
        id: best.id,
        start: cur.start,
        carried: best.prev.conversation.length,
        systemChanged: best.prev.system !== cur.system,
        toolsChanged: JSON.stringify(best.view.tools ?? []) !== JSON.stringify(current.view.tools ?? []),
      }
    }
  }

  // Compaction, a sliding window or an edited retry: this agent has an earlier turn, but this
  // request no longer extends it.
  return sameTag.length > 0 ? { kind: 'rewritten', id: sameTag[0].id } : null
}

const SYSTEM_ROLES = new Set(['system', 'developer'])

// ponytail: JSON key order is stable because both sides come from the same extractor.
function split(view: RenderedView): { system: string; start: number; conversation: string[] } {
  const start = view.messages.findIndex((m) => !SYSTEM_ROLES.has(m.role))
  const lead = start === -1 ? view.messages.length : start
  return {
    system: JSON.stringify([view.system ?? '', ...view.messages.slice(0, lead)]),
    start: lead,
    conversation: view.messages.slice(lead).map((m) => JSON.stringify(m)),
  }
}

function isStrictPrefix(prefix: string[], full: string[]): boolean {
  return prefix.length > 0 && prefix.length < full.length && prefix.every((m, i) => m === full[i])
}

function sameTags(tags: string[]): (other: string[]) => boolean {
  const wanted = [...tags].sort().join('\n')
  return (other) => [...other].sort().join('\n') === wanted
}
