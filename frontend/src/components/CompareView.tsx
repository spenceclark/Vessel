import { useCallback, useEffect, useMemo, useState, type ReactNode } from 'react'
import { useMutation, useQueries, useQuery, useQueryClient } from '@tanstack/react-query'
import { ApiError, api } from '@/api/client'
import { AGGREGATE_QUERY_ROOT, requestDetailQueryKey } from '@/api/queryKeys'
import { MAX_SCORE, MIN_SCORE, type RequestDetail } from '@/api/types'
import type { InFlightRequest } from '@/api/useEvents'
import { ErrorState } from '@/components/ui/ErrorState'
import { Button } from '@/components/ui/button'
import { MessageView } from '@/components/MessageView'
import { PrettyJson } from '@/components/PrettyJson'
import { renderRequest, renderResponse } from '@/render'
import { formatMs, formatTokPerSec, formatTokenCount } from '@/lib/format'
import { findHeader } from '@/lib/headers'
import { cn } from '@/lib/utils'

/**
 * #48 — a comparison is an original plus the members of one replay fan. A pair is the
 * `members.length === 1` case and renders exactly as it did in phase 5; everything wider is
 * the same view with more columns.
 */
export function CompareView({ originalId, replayIds, inFlight = [], onClose }: {
  originalId: number
  replayIds: number[]
  inFlight?: InFlightRequest[]
  onClose: () => void
}) {
  // The selection carries the ids known when Compare was opened, which for a fan still
  // firing is not the whole fan. Subscribing to the original's replay list — the same query
  // a completion already invalidates — lets a member that finishes while this view is open
  // become a column instead of vanishing with its pending one.
  const replaysQuery = useQuery({
    queryKey: ['replays', originalId],
    queryFn: () => api.getReplays(originalId),
  })
  const summaries = useMemo(() => replaysQuery.data ?? [], [replaysQuery.data])
  const selectedGroup = useMemo(
    () => summaries.find((summary) => replayIds.includes(summary.id))?.replayGroup ?? null,
    [summaries, replayIds],
  )
  const memberIds = useMemo(() => {
    const live = selectedGroup === null
      ? []
      : summaries.filter((summary) => summary.replayGroup === selectedGroup).map((summary) => summary.id)
    // Union with the selection: a fan opened before its list refetches still shows.
    return [...new Set([...replayIds, ...live])].sort((a, b) => a - b)
  }, [summaries, selectedGroup, replayIds])

  const queries = useQueries({
    queries: [originalId, ...memberIds].map((id) => ({
      queryKey: requestDetailQueryKey(id),
      queryFn: () => api.getRequest(id),
    })),
  })

  if (queries.some((query) => query.isLoading)) return <div className="p-4 text-sm text-text-muted">Loading comparison…</div>
  if (queries.some((query) => query.isError || !query.data)) {
    return <ErrorState message="Failed to load this replay pair." onRetry={() => { for (const query of queries) void query.refetch() }} />
  }

  const [original, ...members] = queries.map((query) => query.data!)
  if (members.length === 0 || members.some((member) => member.replayOf !== original.id)) {
    return <ErrorState message="These requests are not a direct replay pair." onRetry={onClose} />
  }

  // Fire order is id order within a fan, so the columns read left to right as they were sent.
  const ordered = [...members].sort((a, b) => a.id - b.id)
  const group = selectedGroup ?? ordered.find((member) => member.replayGroup != null)?.replayGroup ?? null
  const pending = inFlight.filter((item) =>
    item.replayOf === original.id && (group == null || item.replayGroup === group))

  return <CompareBody original={original} members={ordered} pending={pending} onClose={onClose} />
}

function CompareBody({ original, members, pending, onClose }: {
  original: RequestDetail
  members: RequestDetail[]
  pending: InFlightRequest[]
  onClose: () => void
}) {
  const pair = members.length === 1
  const diff = useMemo(() => mergedDiff(original, members), [original, members])
  const requestView = useMemo(() => renderRequest(original), [original])
  // The original is a scorable column like any other — that is what makes "did the swap
  // beat what I already had" answerable.
  const columns = useMemo(() => [original, ...members], [original, members])
  const { setScore, error } = useScore(original.id)
  // Scoring is a volume activity (15 prompts x 5 columns is 75 clicks), so the focused
  // column takes 1-5 from the keyboard. Same pattern as RequestList's arrow keys.
  const [focused, setFocused] = useState(0)

  useEffect(() => {
    const handleKeyDown = (event: KeyboardEvent) => {
      const active = document.activeElement
      if (
        active instanceof HTMLElement
        && (active.tagName === 'INPUT' || active.tagName === 'TEXTAREA' || active.isContentEditable)
      ) {
        return
      }

      if (event.key === 'ArrowLeft' || event.key === 'ArrowRight') {
        const next = focused + (event.key === 'ArrowLeft' ? -1 : 1)
        if (next < 0 || next >= columns.length) return
        event.preventDefault()
        setFocused(next)
        return
      }

      const column = columns[focused]
      if (!column) return
      if (event.key >= String(MIN_SCORE) && event.key <= String(MAX_SCORE)) {
        event.preventDefault()
        setScore(column.id, Number(event.key))
      } else if (event.key === '0' || event.key === 'Backspace') {
        event.preventDefault()
        setScore(column.id, null)
      }
    }

    window.addEventListener('keydown', handleKeyDown)
    return () => window.removeEventListener('keydown', handleKeyDown)
  }, [columns, focused, setScore])

  const columnProps = (detail: RequestDetail, index: number) => ({
    score: detail.score,
    focused: focused === index,
    onFocus: () => setFocused(index),
    onScore: (next: number | null) => setScore(detail.id, next),
  })

  return (
    <div className="flex h-full flex-col">
      <div className="flex items-center justify-between border-b border-border px-3 py-2">
        <div>
          <div className="font-mono text-sm text-text">#{original.id} → {members.map((member) => `#${member.id}`).join(', ')}</div>
          <div className="text-xs text-text-muted">
            {original.backend} / {original.model ?? '—'} → {members.map((member) => `${member.backend} / ${member.model ?? '—'}`).join(', ')}
          </div>
        </div>
        <Button variant="ghost" onClick={onClose}>Close</Button>
      </div>
      <div className="min-h-0 flex-1 overflow-y-auto p-3">
        {error && <p className="mb-2 rounded-control bg-[color-mix(in_srgb,var(--color-danger)_12%,transparent)] p-2 text-sm text-danger">{error}</p>}
        <MetricsTable original={original} members={members} />
        <RequestPanel detail={original} view={requestView} pair={pair} members={members} diff={diff} />
        <ResponseGrid original={original} members={members} pending={pending} pair={pair} columnProps={columnProps} />
      </div>
    </div>
  )
}

/**
 * Metrics on the same axis as the response columns: six rows whatever N is, so a wide fan
 * is scanned left to right rather than down sixteen rows of cards.
 */
function MetricsTable({ original, members }: { original: RequestDetail; members: RequestDetail[] }) {
  return (
    <section>
      <h3 className="mb-2 text-xs font-[550] uppercase tracking-[0.06em] text-text-muted">Metrics</h3>
      <div className="overflow-x-auto rounded-control bg-surface-2 p-2.5">
        <table className="min-w-full text-xs">
          <thead>
            <tr className="text-text-muted">
              <th className="pr-3 text-left font-normal">metric</th>
              <th className="pr-3 text-left font-normal">#{original.id}</th>
              {members.map((member) => <th key={member.id} className="pr-3 text-left font-normal">#{member.id}</th>)}
            </tr>
          </thead>
          <tbody>
            {METRIC_ROWS.map((row) => (
              <tr key={row.label} className="align-top">
                <td className="py-1 pr-3 font-[550] uppercase tracking-[0.06em] text-text-muted">{row.label}</td>
                <td className="py-1 pr-3 font-mono">{row.format(row.pick(original))}</td>
                {members.map((member) => (
                  <td key={member.id} className="py-1 pr-3">
                    <MetricCell
                      value={row.format(row.pick(member))}
                      delta={delta(row.pick(original), row.pick(member))}
                      formatDelta={row.format}
                    />
                  </td>
                ))}
              </tr>
            ))}
            <tr className="align-top">
              <td className="py-1 pr-3 font-[550] uppercase tracking-[0.06em] text-text-muted">Stop reason</td>
              <td className="py-1 pr-3 font-mono">{original.stopReason ?? '—'}</td>
              {members.map((member) => (
                <td key={member.id} className="py-1 pr-3">
                  <MetricCell value={member.stopReason ?? '—'} delta={null} />
                </td>
              ))}
            </tr>
          </tbody>
        </table>
      </div>
    </section>
  )
}

function ResponseGrid({ original, members, pending, pair, columnProps }: {
  original: RequestDetail
  members: RequestDetail[]
  pending: InFlightRequest[]
  pair: boolean
  columnProps: (detail: RequestDetail, index: number) => ColumnControls
}) {
  const originalColumn = (
    <ResponseColumn
      title={`Original #${original.id}`}
      subtitle={`${original.model ?? '—'} · ${original.backend}`}
      detail={original}
      controls={columnProps(original, 0)}
    />
  )
  const memberColumns = members.map((member, index) => (
    <ResponseColumn
      key={member.id}
      title={`Replay #${member.id}`}
      subtitle={[member.model ?? '—', member.backend, variationLabel(member)].filter(Boolean).join(' · ')}
      detail={member}
      controls={columnProps(member, index + 1)}
    />
  ))

  // A pair keeps its two-up grid; anything wider scrolls inside itself (ui-spec's
  // wide-content rule, never the page body). Both route through the same column, so the
  // score control exists in either layout.
  return pair ? (
    <section className="mt-5 grid gap-3 lg:grid-cols-2">
      {originalColumn}
      {memberColumns}
      {pending.map((item) => <PendingPanel key={item.seq} item={item} />)}
    </section>
  ) : (
    <section className="mt-5 flex gap-3 overflow-x-auto">
      {originalColumn}
      {memberColumns}
      {pending.map((item) => <PendingPanel key={item.seq} item={item} />)}
    </section>
  )
}

type ColumnControls = {
  score: number | null
  focused: boolean
  onFocus: () => void
  onScore: (score: number | null) => void
}

function ResponseColumn({ title, subtitle, detail, controls }: {
  title: string
  subtitle: string
  detail: RequestDetail
  controls: ColumnControls
}) {
  const view = useMemo(() => renderResponse(detail), [detail])
  return (
    <div
      className={cn('relative min-w-[320px] flex-1 rounded-control border', controls.focused ? 'border-accent' : 'border-border')}
      onClick={controls.onFocus}
    >
      {controls.focused && <span className="absolute left-0 top-0 h-full w-0.5 bg-accent" aria-hidden="true" />}
      <ColumnHeader title={title} subtitle={subtitle}>
        <ScoreControl label={title} score={controls.score} onScore={controls.onScore} />
      </ColumnHeader>
      {view ? <MessageView view={view} /> : <PrettyJson body={detail.responseBody} emptyLabel="No response body" />}
    </div>
  )
}

function ColumnHeader({ title, subtitle, children }: { title: string; subtitle: string; children?: ReactNode }) {
  return (
    <div className="border-b border-border px-3 py-2">
      <div className="font-mono text-sm text-text">{title}</div>
      <div className="truncate font-mono text-xs text-text-muted">{subtitle}</div>
      <div className="mt-1 flex h-6 items-center" data-slot="score">{children}</div>
    </div>
  )
}

/**
 * #49 — five segmented buttons filled up to the current value. Clicking the current value
 * clears it, so setting and unsetting are the same gesture; no hover stars, no half points.
 */
function ScoreControl({ label, score, onScore }: { label: string; score: number | null; onScore: (score: number | null) => void }) {
  return (
    <div className="flex gap-0.5" role="group" aria-label={`Score ${label}`}>
      {Array.from({ length: MAX_SCORE - MIN_SCORE + 1 }, (_, i) => i + MIN_SCORE).map((value) => (
        <button
          key={value}
          type="button"
          aria-label={`Score ${value}`}
          aria-pressed={score === value}
          className={cn(
            'h-5 w-5 rounded-control border font-mono text-xs',
            score != null && value <= score
              ? 'border-accent bg-accent text-accent-fg'
              : 'border-border text-text-muted hover:bg-surface-2',
          )}
          onClick={(event) => {
            event.stopPropagation()
            onScore(score === value ? null : value)
          }}
        >
          {value}
        </button>
      ))}
    </div>
  )
}

function PendingPanel({ item }: { item: InFlightRequest }) {
  // Nothing to score yet, so the slot stays empty rather than offering a control that 404s.
  return (
    <div className="min-w-[320px] flex-1 rounded-control border border-dashed border-border">
      <ColumnHeader title={`Replay #${item.seq}…`} subtitle={`${item.model ?? '—'} · ${item.backend}`} />
      <p className="p-3 text-sm text-text-muted">In flight…</p>
    </div>
  )
}

/**
 * #49 — optimistic write of the scored row's cached detail, rolled back on failure. The
 * replay list and the aggregate roots are invalidated so the Reports leaderboard reflects a
 * score without a manual refresh; there is no SSE event, so a second tab is stale until its
 * own next refetch.
 */
function useScore(originalId: number) {
  const queryClient = useQueryClient()
  const [error, setError] = useState<string | null>(null)
  const mutation = useMutation({
    mutationFn: ({ id, score }: { id: number; score: number | null }) => api.setScore(id, score),
    onMutate: async ({ id, score }) => {
      setError(null)
      const key = requestDetailQueryKey(id)
      await queryClient.cancelQueries({ queryKey: key })
      const previous = queryClient.getQueryData<RequestDetail>(key)
      if (previous) queryClient.setQueryData<RequestDetail>(key, { ...previous, score })
      return { key, previous }
    },
    onError: (err, _variables, context) => {
      if (context?.previous) queryClient.setQueryData(context.key, context.previous)
      setError(err instanceof ApiError ? err.message : 'Could not save that score.')
    },
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['replays', originalId] })
      void queryClient.invalidateQueries({ queryKey: AGGREGATE_QUERY_ROOT })
    },
  })

  const setScore = useCallback(
    (id: number, score: number | null) => mutation.mutate({ id, score }),
    [mutation],
  )
  return { setScore, error }
}

function RequestPanel({ detail, view, pair, members, diff }: {
  detail: RequestDetail
  view: ReturnType<typeof renderRequest>
  pair: boolean
  members: RequestDetail[]
  diff: MergedDiffRow[]
}) {
  return (
    <section className="mt-5 rounded-control border border-border">
      <div className="border-b border-border px-3 py-2">
        <h3 className="text-xs font-[550] uppercase tracking-[0.06em] text-text-muted">Request</h3>
        {diff.length === 0 ? <p className="mt-1 text-sm text-text-muted">No differing parameters.</p> : pair ? (
          <dl className="mt-1 space-y-1 font-mono text-xs">
            {diff.map((row) => (
              <div key={row.name} className="grid grid-cols-[minmax(80px,auto)_1fr] gap-2">
                <dt className="text-text-muted">{row.name}</dt>
                <dd>{row.cells[0]?.auto && row.cells[0].after === row.before
                  ? <>{row.before} <span className="text-text-muted">(auto)</span></>
                  : <>{row.before} <span className="text-text-muted">→</span> {row.cells[0]?.after ?? row.before}
                    {row.cells[0]?.auto && <span className="text-text-muted"> (auto)</span>}</>}</dd>
              </div>
            ))}
          </dl>
        ) : (
          <div className="mt-1 overflow-x-auto">
            <table className="min-w-full font-mono text-xs">
              <thead>
                <tr className="text-text-muted">
                  <th className="pr-3 text-left font-normal">parameter</th>
                  <th className="pr-3 text-left font-normal">#{detail.id}</th>
                  {members.map((member) => <th key={member.id} className="pr-3 text-left font-normal">#{member.id}</th>)}
                </tr>
              </thead>
              <tbody>
                {diff.map((row) => (
                  <tr key={row.name}>
                    <td className="pr-3 text-text-muted">{row.name}</td>
                    <td className="pr-3">{row.before}</td>
                    {row.cells.map((cell, index) => (
                      <td key={index} className="pr-3">
                        {cell === null ? <span className="text-text-muted">—</span> : cell.auto
                          ? <>{cell.after} <span className="text-text-muted">(auto)</span></>
                          : cell.after}
                      </td>
                    ))}
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>
      {view ? <MessageView view={view} /> : <PrettyJson body={detail.requestBody} emptyLabel="No request body" />}
    </section>
  )
}

/** One member's value with its delta from the original underneath. */
export function MetricCell({ value, delta, formatDelta }: { value: string; delta: number | null; formatDelta?: (value: number | null) => string }) {
  const sign = delta == null || delta === 0
    ? '—'
    : `${delta > 0 ? '+' : '−'}${formatDelta ? formatDelta(Math.abs(delta)) : Math.round(Math.abs(delta) * 10) / 10}`
  return <div className="font-mono"><div>{value}</div><div className="text-text-muted">Δ {sign}</div></div>
}

const METRIC_ROWS: { label: string; pick: (detail: RequestDetail) => number | null; format: (value: number | null) => string }[] = [
  { label: 'Duration', pick: (d) => d.durationMs, format: formatMs },
  { label: 'TTFT', pick: (d) => d.ttftMs, format: formatMs },
  { label: 'Tok/s', pick: (d) => d.tokPerSec, format: formatTokPerSec },
  { label: 'Tokens in', pick: (d) => d.tokensIn, format: (v) => formatTokenCount(v, false) },
  { label: 'Tokens out', pick: (d) => d.tokensOut, format: (v) => formatTokenCount(v, false) },
  // #49 — the header control is where you *set* a score, next to the text you are judging;
  // this row is where you *scan* it against tok/s. Same value, two jobs.
  { label: 'Score', pick: (d) => d.score, format: (v) => (v == null ? '—' : `${v}/5`) },
]

function delta(before: number | null, after: number | null): number | null {
  return before == null || after == null ? null : after - before
}

/**
 * #48 — a column's label is the recorded fact of what varied: the patch's leaf key/value in
 * params mode. Never derived by diffing bodies. In models mode the model *is* the variation,
 * and the header already shows it, so there is nothing to add.
 */
function variationLabel(member: RequestDetail): string | null {
  return member.replayPatch != null ? patchLeaf(member.replayPatch) : null
}

function patchLeaf(patch: string): string | null {
  try {
    let node: unknown = JSON.parse(patch)
    let entry: [string, unknown] | undefined
    while (typeof node === 'object' && node !== null && !Array.isArray(node)) {
      const entries = Object.entries(node as Record<string, unknown>)
      if (entries.length === 0) break
      entry = entries[0]
      node = entry[1]
    }
    return entry ? `${entry[0]} ${JSON.stringify(entry[1])}` : null
  } catch { return null }
}

type ParamDiffRow = { name: string; before: string; after: string; auto?: boolean }

type MergedDiffRow = { name: string; before: string; cells: (ParamDiffRow | null)[] }

/** Rows are the keys that differ from the original in *any* member; one cell per column. */
function mergedDiff(original: RequestDetail, members: RequestDetail[]): MergedDiffRow[] {
  const perMember = members.map((member) => new Map(parameterDiff(original, member).map((row) => [row.name, row])))
  const names = [...new Set(perMember.flatMap((rows) => [...rows.keys()]))].sort((a, b) => a.localeCompare(b))
  return names.map((name) => ({
    name,
    before: perMember.find((rows) => rows.has(name))!.get(name)!.before,
    cells: perMember.map((rows) => rows.get(name) ?? null),
  }))
}

/**
 * #28 — the openai-chat dialect fix-ups ReplayEndpoint.cs may apply while composing a
 * replay (rename, never copy). Ids are hand-mirrored from `ReplayEndpoint.CurrentFixupId`
 * / `LegacyFixupId` — keep both sides in sync by hand, same as the rest of this file.
 */
const OPENAI_CHAT_RENAME_RULES: { id: string; from: string; to: string }[] = [
  { id: 'openai-chat:max_tokens->max_completion_tokens', from: 'max_tokens', to: 'max_completion_tokens' },
  { id: 'openai-chat:max_completion_tokens->max_tokens', from: 'max_completion_tokens', to: 'max_tokens' },
]

function parameterDiff(original: RequestDetail, replay: RequestDetail): ParamDiffRow[] {
  const a = parseObject(original.requestBody?.text)
  const b = parseObject(replay.requestBody?.text)
  const rows = new Map<string, ParamDiffRow>()
  const patch = replay.replayPatch != null ? parseObject(replay.replayPatch) : null

  if (patch) {
    // The patch *is* the list of what changed, so the rows are its leaf paths — no body
    // deep-diff. A top-level diff would render Ollama's whole `options` object per column,
    // when the one thing that varied is `options.temperature`.
    for (const path of leafPaths(patch)) {
      const patched = valueAt(patch, path)
      rows.set(path, {
        name: path,
        before: display(a ? valueAt(a, path) : ABSENT),
        after: patched.value === null ? '(removed)' : display(b ? valueAt(b, path) : patched),
      })
    }

    // A params fan may also carry a model override; the patch cannot mention `model`
    // (the endpoint rejects that), so it would otherwise vanish from this panel.
    if (replay.model !== original.model) {
      rows.set('model', {
        name: 'model',
        before: JSON.stringify(original.model) ?? 'undefined',
        after: JSON.stringify(replay.model) ?? 'undefined',
      })
    }
  } else if (a && b) {
    const names = new Set([...Object.keys(a), ...Object.keys(b)])
    for (const name of names) {
      const before = JSON.stringify(a[name]) ?? 'undefined'
      const after = JSON.stringify(b[name]) ?? 'undefined'
      if (before !== after) rows.set(name, { name, before, after })
    }
  }

  // The (auto) label must come from this recorded fact, not from pattern-matching the diff
  // itself — a user-supplied difference that happens to look like a rename must not get it.
  // Checked independently of whether the replay body parsed above: renaming toward the
  // longer spelling can push an already-near-cap replay past the capture cap, and a
  // confirmed fix-up must still show — from the complete original's value, since the
  // fix-up preserves it unchanged.
  const appliedFixups = new Set((findHeader(replay.requestHeaders, 'x-vessel-replay-fixups') ?? '').split(',').filter(Boolean))
  for (const rule of OPENAI_CHAT_RENAME_RULES) {
    if (!appliedFixups.has(rule.id)) continue
    // An absent original stays absent: copying the sent value back would render an *added*
    // limit as a no-op.
    const before = a && rule.from in a ? JSON.stringify(a[rule.from]) ?? 'undefined' : null
    // The value actually sent, read under the *target* spelling — a params fan can patch the
    // very key the fix-up then renames, and showing the original's value on both sides would
    // render a token-limit sweep as five copies of the number nobody swept. When the replay
    // body cannot be read (truncated, or pushed past the cap by the longer spelling) the
    // recorded patch still knows what was set; only with no patch either does the fix-up's
    // own guarantee — it renames without altering the value — make the original the answer.
    const after = b && rule.to in b
      ? JSON.stringify(b[rule.to]) ?? 'undefined'
      : patch && rule.from in patch
        ? JSON.stringify(patch[rule.from]) ?? 'undefined'
        : before
    if (before === null && after === null) continue
    rows.delete(rule.from)
    rows.delete(rule.to)
    rows.set(rule.from, {
      name: `${rule.from} → ${rule.to}`,
      before: before ?? '—',
      after: after ?? '—',
      auto: true,
    })
  }

  return [...rows.values()].sort((x, y) => x.name.localeCompare(y.name))
}

type Lookup = { present: boolean; value: unknown }

const ABSENT: Lookup = { present: false, value: undefined }

/** Dotted paths to every leaf of a merge patch; a `null` leaf is a deletion, still a leaf. */
function leafPaths(patch: Record<string, unknown>, prefix = ''): string[] {
  return Object.entries(patch).flatMap(([key, value]) => {
    const path = prefix === '' ? key : `${prefix}.${key}`
    return typeof value === 'object' && value !== null && !Array.isArray(value)
      ? leafPaths(value as Record<string, unknown>, path)
      : [path]
  })
}

function valueAt(root: Record<string, unknown>, path: string): Lookup {
  let node: unknown = root
  for (const segment of path.split('.')) {
    if (typeof node !== 'object' || node === null || Array.isArray(node) || !(segment in node)) return ABSENT
    node = (node as Record<string, unknown>)[segment]
  }
  return { present: true, value: node }
}

function display(lookup: Lookup): string {
  return lookup.present ? JSON.stringify(lookup.value) ?? 'undefined' : '—'
}

function parseObject(text?: string): Record<string, unknown> | null {
  if (!text) return null
  try {
    const value: unknown = JSON.parse(text)
    return typeof value === 'object' && value !== null && !Array.isArray(value) ? value as Record<string, unknown> : null
  } catch { return null }
}
