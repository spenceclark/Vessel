import { useCallback, useEffect, useState } from 'react'
import { useQuery, useQueryClient, type InfiniteData } from '@tanstack/react-query'
import { api } from '@/api/client'
import { EMPTY_FILTERS, filtersActive, type RequestFilters, type RequestListResponse, type SessionScope } from '@/api/types'
import { useEvents, useNowTick } from '@/api/useEvents'
import { StatsBar } from '@/components/StatsBar'
import { FilterBar } from '@/components/FilterBar'
import { RequestList } from '@/components/RequestList'
import { DetailPane } from '@/components/DetailPane'
import { InFlightDetailPane } from '@/components/InFlightDetailPane'

/**
 * ui-spec.md §9.1 — selection spans both a completed row (by DB id) and an in-flight
 * request (by SSE `seq`, which has no id yet). On `completed`, a currently-selected
 * in-flight seq hands over to the real row id so the detail pane replaces itself in
 * place rather than reverting to empty.
 */
export type Selection = { kind: 'row'; id: number } | { kind: 'inflight'; seq: number }

/** D6/D3 — one screen: StatsBar / FilterBar / RequestList / DetailPane. No router. */
export default function App() {
  const queryClient = useQueryClient()
  const [scope, setScope] = useState<SessionScope | null>(null)
  const [currentSessionId, setCurrentSessionId] = useState<number | null>(null)
  const [selection, setSelection] = useState<Selection | null>(null)
  const [filters, setFilters] = useState<RequestFilters>(EMPTY_FILTERS)
  const [newSinceFilter, setNewSinceFilter] = useState(0)

  const sessionsQuery = useQuery({ queryKey: ['sessions'], queryFn: api.listSessions })

  // Default view = current session (D6): the newest session marker, once it's known.
  useEffect(() => {
    if (currentSessionId !== null) return
    const newest = sessionsQuery.data?.[0]
    if (newest) {
      setCurrentSessionId(newest.id)
      setScope(newest.id)
    }
  }, [sessionsQuery.data, currentSessionId])

  const queryKey = ['requests', scope, filters] as const
  const filtered = filtersActive(filters)

  const queryKeySignature = JSON.stringify(queryKey)
  useEffect(() => {
    setNewSinceFilter(0)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [queryKeySignature])

  const { inFlight } = useEvents((row, seq) => {
    // Selection handover (ui-spec.md §9.1): the in-flight detail replaces itself with
    // the real row in place, rather than the pane reverting to empty on completion.
    setSelection((sel) => (sel?.kind === 'inflight' && sel.seq === seq && row ? { kind: 'row', id: row.id } : sel))

    if (!row || scope === null) return
    if (scope !== 'all' && row.sessionId !== scope) return

    if (filtered) {
      setNewSinceFilter((n) => n + 1)
      return
    }

    // A refetch already in flight (e.g. C2's reconnect invalidation) would clobber a
    // splice made now — its snapshot was taken before this row existed, so it overwrites
    // the cache without it once it resolves. `completed` only fires after the writer has
    // inserted the row, so invalidating instead queues a fresh refetch behind the current
    // one that's guaranteed to include it.
    if (queryClient.getQueryState(queryKey)?.fetchStatus === 'fetching') {
      void queryClient.invalidateQueries({ queryKey })
      return
    }

    queryClient.setQueryData<InfiniteData<RequestListResponse, number | undefined>>(queryKey, (old) => {
      if (!old) return old
      const first = old.pages[0]
      if (first.rows.some((r) => r.id === row.id)) return old
      const pages = [...old.pages]
      pages[0] = { ...first, rows: [row, ...first.rows] }
      return { ...old, pages }
    })
  })

  const now = useNowTick(250)

  const handleReset = useCallback(async () => {
    const session = await api.createSession()
    setCurrentSessionId(session.id)
    setScope(session.id)
    setSelection(null)
    await queryClient.invalidateQueries({ queryKey: ['sessions'] })
  }, [queryClient])

  return (
    <div className="h-screen overflow-hidden bg-canvas">
      <div className="mx-auto flex h-full max-w-[1600px] flex-col gap-3 p-4 lg:p-6">
        <StatsBar
          scope={scope}
          currentSessionId={currentSessionId}
          onScopeChange={setScope}
          onReset={handleReset}
        />
        <div className="flex min-h-0 flex-1 gap-3">
          <div className="flex w-[420px] shrink-0 flex-col overflow-hidden rounded-panel border border-border bg-surface shadow-panel">
            <FilterBar scope={scope} filters={filters} onFiltersChange={setFilters} />
            <div className="min-h-0 flex-1">
              <RequestList
                scope={scope}
                filters={filters}
                inFlight={inFlight}
                now={now}
                newSinceFilter={newSinceFilter}
                onClearNewSinceFilter={() => setNewSinceFilter(0)}
                selection={selection}
                onSelectRow={(id) => setSelection({ kind: 'row', id })}
                onSelectInFlight={(seq) => setSelection({ kind: 'inflight', seq })}
              />
            </div>
          </div>
          <div className="min-w-0 flex-1 overflow-hidden rounded-panel border border-border bg-surface shadow-panel">
            {selection?.kind === 'inflight' ? (
              <InFlightDetailPane item={inFlight.get(selection.seq) ?? null} now={now} />
            ) : (
              <DetailPane id={selection?.kind === 'row' ? selection.id : null} />
            )}
          </div>
        </div>
      </div>
    </div>
  )
}
