import { useCallback, useEffect, useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from '@/api/client'
import { REQUEST_DETAIL_QUERY_ROOT } from '@/api/queryKeys'
import { EMPTY_FILTERS, type RequestDetail, type RequestFilters, type SessionScope } from '@/api/types'
import { useLiveHistory } from '@/api/useLiveHistory'
import { CaptureHealthBanner } from '@/components/CaptureHealthBanner'
import { BindAddressBanner } from '@/components/BindAddressBanner'
import { StatsBar } from '@/components/StatsBar'
import { FilterBar } from '@/components/FilterBar'
import { RequestList } from '@/components/RequestList'
import { DetailPane } from '@/components/DetailPane'
import { InFlightDetailPane } from '@/components/InFlightDetailPane'
import { CompareView } from '@/components/CompareView'

/**
 * ui-spec.md §9.1 — selection spans both a completed row (by DB id) and an in-flight
 * request (by SSE `seq`, which has no id yet). On `completed`, a currently-selected
 * in-flight seq hands over to the real row id so the detail pane replaces itself in
 * place rather than reverting to empty.
 */
export type Selection =
  | { kind: 'row'; id: number }
  | { kind: 'inflight'; seq: number }
  | { kind: 'compare'; originalId: number; replayId: number }

/** D6/D3 — one screen: StatsBar / FilterBar / RequestList / DetailPane. No router. */
export default function App() {
  const queryClient = useQueryClient()
  const [scope, setScope] = useState<SessionScope | null>(null)
  const [currentSessionId, setCurrentSessionId] = useState<number | null>(null)
  const [selection, setSelection] = useState<Selection | null>(null)
  const [filters, setFilters] = useState<RequestFilters>(EMPTY_FILTERS)

  const sessionsQuery = useQuery({ queryKey: ['sessions'], queryFn: api.listSessions })

  // Default view = current session (D6): the newest session marker, once it's known.
  useEffect(() => {
    if (currentSessionId !== null) return
    const newest = sessionsQuery.data?.[0]
    if (newest) {
      // oxlint-disable-next-line react/set-state-in-effect -- this synchronizes the initial server-owned session into local view state exactly once.
      setCurrentSessionId(newest.id)
      setScope(newest.id)
    }
  }, [sessionsQuery.data, currentSessionId])

  // R10/R11/D05 — live history (in-flight map, completion merging, reconciliation) is one
  // model, owned by one hook. App only supplies scope/filters and handles selection.
  const { inFlight, connected, newSinceFilter, clearNewSinceFilter } = useLiveHistory({
    scope,
    filters,
    onCompleted: (row, seq) => {
      if (row?.replayOf != null) {
        void queryClient.invalidateQueries({ queryKey: ['replays', row.replayOf] })
      }
      // Selection handover (ui-spec.md §9.1): the in-flight detail replaces itself with
      // the real row in place, rather than the pane reverting to empty on completion.
      setSelection((sel) => (sel?.kind === 'inflight' && sel.seq === seq && row ? { kind: 'row', id: row.id } : sel))
    },
  })

  const handleReset = useCallback(async () => {
    const session = await api.createSession()
    setCurrentSessionId(session.id)
    setScope(session.id)
    setSelection(null)
    await queryClient.invalidateQueries({ queryKey: ['sessions'] })
  }, [queryClient])

  // R14a — a clear (all/before) can delete the currently-selected row. Left alone, the
  // detail pane keeps showing whatever `['request', id]` last cached: stale at best, and
  // actively wrong once SQLite reuses that id for an unrelated later capture (R14b — no
  // schema change to prevent reuse; documented in architecture.md §6 as a known caveat).
  // Every clear evicts every cached detail unconditionally (cheap, and the only way to be
  // sure none of them can resurface via reuse); the selection itself only clears when the
  // clear actually reached the selected row. (The live list/buffer purge is server-driven,
  // via the in-band `cleared` SSE event in useLiveHistory — R23/H0a — not this callback.)
  const handleDataCleared = useCallback(
    (clearedScope: { all: true } | { before: string }) => {
      const cachedDetails = queryClient.getQueriesData<RequestDetail>({ queryKey: REQUEST_DETAIL_QUERY_ROOT })
      queryClient.removeQueries({ queryKey: REQUEST_DETAIL_QUERY_ROOT })

      setSelection((sel) => {
        if (sel?.kind !== 'row') return sel // an in-flight selection isn't stored history
        if ('all' in clearedScope) return null

        const cached = cachedDetails.find(([key]) => key[1] === sel.id)?.[1]
        // Unknown startedAt (never fetched, or evicted before this ran) is treated as
        // "assume it was reached" — the safe default when R14 is specifically about not
        // trusting stale state.
        const survived = cached && new Date(cached.startedAt) >= new Date(clearedScope.before)
        return survived ? sel : null
      })
    },
    [queryClient],
  )

  return (
    <div className="h-screen overflow-hidden bg-canvas">
      <div className="mx-auto flex h-full max-w-[1600px] flex-col gap-3 p-4 lg:p-6">
        <CaptureHealthBanner />
        <BindAddressBanner />
        <StatsBar
          scope={scope}
          currentSessionId={currentSessionId}
          onScopeChange={setScope}
          onReset={handleReset}
          onDataCleared={handleDataCleared}
          connected={connected}
        />
        <div className="flex min-h-0 flex-1 gap-3">
          <div className="flex w-[420px] shrink-0 flex-col overflow-hidden rounded-panel border border-border bg-surface shadow-panel">
            <FilterBar scope={scope} filters={filters} onFiltersChange={setFilters} />
            {/* R12 — a guaranteed floor so the list panel's own content (filter controls
                incl. the tag picker) can never squeeze the request list to nothing; the
                tag picker's own max-height cap is the primary guard, this is the backstop. */}
            <div className="min-h-[160px] flex-1">
              <RequestList
                scope={scope}
                filters={filters}
                inFlight={inFlight}
                newSinceFilter={newSinceFilter}
                onClearNewSinceFilter={clearNewSinceFilter}
                selection={selection}
                onSelectRow={(id) => setSelection({ kind: 'row', id })}
                onSelectInFlight={(seq) => setSelection({ kind: 'inflight', seq })}
              />
            </div>
          </div>
          <div className="min-w-0 flex-1 overflow-hidden rounded-panel border border-border bg-surface shadow-panel">
            {selection?.kind === 'compare' ? (
              <CompareView originalId={selection.originalId} replayId={selection.replayId} onClose={() => setSelection({ kind: 'row', id: selection.replayId })} />
            ) : selection?.kind === 'inflight' ? (
              <InFlightDetailPane item={inFlight.find((i) => i.seq === selection.seq) ?? null} />
            ) : (
              <DetailPane id={selection?.kind === 'row' ? selection.id : null} onCompare={(originalId, replayId) => setSelection({ kind: 'compare', originalId, replayId })} />
            )}
          </div>
        </div>
      </div>
    </div>
  )
}
