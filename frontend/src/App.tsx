import { useCallback, useEffect, useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from '@/api/client'
import { REQUEST_DETAIL_QUERY_ROOT, REQUESTS_QUERY_ROOT } from '@/api/queryKeys'
import { EMPTY_FILTERS, SESSION_LIST_LIMIT, type RequestClearScope, type RequestDetail, type RequestFilters, type SessionDeleteSummary, type SessionInfo, type SessionScope } from '@/api/types'
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
  const [selection, setSelection] = useState<Selection | null>(null)
  const [filters, setFilters] = useState<RequestFilters>(EMPTY_FILTERS)

  const sessionsQuery = useQuery({
    queryKey: ['sessions'],
    queryFn: api.listSessions,
    refetchInterval: 5_000,
  })
  const selectedSession = typeof scope === 'number'
    ? sessionsQuery.data?.find((session) => session.id === scope)
    : undefined
  // Name assignment resolves to the newest exact match. If a legacy/manual reset left
  // duplicate names, only that newest marker may claim the named in-flight traffic.
  const selectedSessionName = selectedSession?.name
    && sessionsQuery.data?.find((session) => session.name === selectedSession.name)?.id === selectedSession.id
    ? selectedSession.name
    : null

  // Default view = the Reset-driven current session. Named sessions may be newer without
  // becoming current, so #29 makes that state explicit instead of inferring it from order.
  useEffect(() => {
    if (scope !== null) return
    const current = sessionsQuery.data?.find((session) => session.isCurrent)
      ?? sessionsQuery.data?.[0]
    if (current) {
      // oxlint-disable-next-line react/set-state-in-effect -- this synchronizes the initial server-owned session into local view state exactly once.
      setScope(current.id)
    }
  }, [sessionsQuery.data, scope])

  // #41 — another tab or API client can delete the session this tab is viewing. Once the
  // invalidated session list confirms it is gone, return to current instead of leaving the
  // picker and list on an unbrowsable orphan scope.
  useEffect(() => {
    if (typeof scope !== 'number' || !sessionsQuery.data) return
    if (sessionsQuery.data.some((session) => session.id === scope)) return
    // #29 — the listing is server-bounded, so absence only means "deleted" when the response
    // came back short. At the limit the viewed session may simply have fallen outside the
    // newest-N window, and yanking the scope would make a live session unreachable here.
    if (sessionsQuery.data.length >= SESSION_LIST_LIMIT) return
    const current = sessionsQuery.data.find((session) => session.isCurrent)
    // oxlint-disable-next-line react/set-state-in-effect -- synchronizes server-owned deletion into the local view.
    setScope(current?.id ?? 'all')
    setSelection(null)
  }, [sessionsQuery.data, scope])

  // R10/R11/D05 — live history (in-flight map, completion merging, reconciliation) is one
  // model, owned by one hook. App only supplies scope/filters and handles selection.
  const { inFlight, connected, newSinceFilter, clearNewSinceFilter } = useLiveHistory({
    scope,
    sessionName: selectedSessionName,
    filters,
    onCompleted: (row, seq) => {
      if (row?.sessionId != null) {
        const knownSessions = queryClient.getQueryData<SessionInfo[]>(['sessions'])
        if (!knownSessions?.some((session) => session.id === row.sessionId)) {
          void queryClient.invalidateQueries({ queryKey: ['sessions'] })
        }
      }
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
    // Seed the returned marker before selecting it. Without this, the orphan-scope effect
    // observes the previous list for one render and immediately bounces Reset back to old current.
    queryClient.setQueryData<SessionInfo[]>(['sessions'], (sessions) => [
      session,
      ...(sessions ?? []).filter((candidate) => candidate.id !== session.id).map((candidate) => ({
        ...candidate,
        isCurrent: false,
      })),
    ])
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
    (clearedScope: RequestClearScope) => {
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

  const handleSessionsDeleted = useCallback((sessionIds: number[]) => {
    const deleted = new Set(sessionIds)
    const cachedDetails = queryClient.getQueriesData<RequestDetail>({ queryKey: REQUEST_DETAIL_QUERY_ROOT })
    queryClient.removeQueries({ queryKey: REQUEST_DETAIL_QUERY_ROOT })

    setSelection((selected) => {
      if (selected?.kind !== 'row') return selected
      const cached = cachedDetails.find(([key]) => key[1] === selected.id)?.[1]
      return cached && !deleted.has(cached.sessionId ?? -1) ? selected : null
    })
    setScope((currentScope) => {
      if (typeof currentScope !== 'number' || !deleted.has(currentScope)) return currentScope
      return sessionsQuery.data?.find((session) => session.isCurrent)?.id ?? 'all'
    })
  }, [queryClient, sessionsQuery.data])

  const handleDeleteSessions = useCallback(async (sessionIds: number[]): Promise<SessionDeleteSummary> => {
    const deletedIds: number[] = []
    const failures: { sessionId: number; message: string }[] = []
    let requestsDeleted = 0
    for (const sessionId of sessionIds) {
      try {
        const result = await api.deleteSession(sessionId)
        deletedIds.push(sessionId)
        requestsDeleted += result.deleted
      } catch (error) {
        failures.push({
          sessionId,
          message: error instanceof Error ? error.message : 'Failed to delete session.',
        })
      }
    }

    if (deletedIds.length > 0) handleSessionsDeleted(deletedIds)
    await Promise.all([
      queryClient.invalidateQueries({ queryKey: REQUESTS_QUERY_ROOT }),
      queryClient.invalidateQueries({ queryKey: ['stats'] }),
      queryClient.invalidateQueries({ queryKey: ['facets'] }),
      queryClient.invalidateQueries({ queryKey: ['sessions'] }),
    ])

    return { sessionsDeleted: deletedIds.length, requestsDeleted, failures }
  }, [handleSessionsDeleted, queryClient])

  return (
    <div className="h-screen overflow-hidden bg-canvas">
      <div className="mx-auto flex h-full max-w-[1600px] flex-col gap-3 p-4 lg:p-6">
        <CaptureHealthBanner />
        <BindAddressBanner />
        <StatsBar
          scope={scope}
          sessions={sessionsQuery.data ?? []}
          onScopeChange={setScope}
          onReset={handleReset}
          onDataCleared={handleDataCleared}
          onDeleteSessions={handleDeleteSessions}
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
