import type { RenderedView } from './types'

/**
 * #147 — which earlier request this one continues. The previous turn is the newest earlier
 * request whose normalised message list is a strict prefix of this one's: same-tag candidates
 * first, then any tag. Prefix matching (rather than "the row before") is what keeps this right
 * under interleaved multi-agent traffic. An identical list (an exact retry) is not a previous
 * turn — there has to be something new.
 *
 * OpenAI Chat and Ollama carry the system prompt as leading `system`/`developer` messages
 * rather than in `view.system`; those count as the system prompt, not the conversation, so a
 * changed prompt is reported as changed rather than breaking the prefix. `start` is where
 * the carried-over messages begin in this request's `messages`.
 */
export type PreviousTurn =
  | { kind: 'match'; id: number; start: number; carried: number; systemChanged: boolean; toolsChanged: boolean }
  | { kind: 'rewritten'; id: number }

export interface TurnCandidate {
  id: number
  tags: string[]
}

/**
 * `candidates` newest first (the list endpoint's order), already scoped to the same session
 * and format. `load` returns a candidate's rendered request, or null when it doesn't render;
 * candidates are loaded one at a time and the search stops at the first match.
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
  const ordered = [...sameTag, ...candidates.filter((c) => !tagKey(c.tags))]

  for (const candidate of ordered) {
    const view = await load(candidate.id)
    if (!view) continue
    const prev = split(view)
    if (!isStrictPrefix(prev.conversation, cur.conversation)) continue
    return {
      kind: 'match',
      id: candidate.id,
      start: cur.start,
      carried: prev.conversation.length,
      systemChanged: prev.system !== cur.system,
      toolsChanged: JSON.stringify(view.tools ?? []) !== JSON.stringify(current.view.tools ?? []),
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
