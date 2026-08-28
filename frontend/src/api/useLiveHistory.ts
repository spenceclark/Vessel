import { useCallback, useEffect, useRef, useState } from 'react'
import { useIsFetching, useQueryClient, type InfiniteData } from '@tanstack/react-query'
import {
  filtersActive,
  type ClearedEvent,
  type RequestFilters,
  type RequestListResponse,
  type SessionScope,
  type Summary,
} from './types'
import { api } from './client'
import { REQUESTS_QUERY_ROOT, requestsQueryKey } from './queryKeys'
import { useEvents, type InFlightRequest } from './useEvents'

/**
 * R10/R11/R22/R23/D05 — one reconciliation model for live rows, rather than independent
 * patches. The pieces have to hold together:
 *
 * 1. **A completion must never be lost across a fetch boundary** (R10). An *initial* list
 *    fetch (no cached data) reuses its existing promise instead of queueing a fresh one, so
 *    a completion arriving while it is unsettled would be dropped. Completions arriving while
 *    any list fetch is unsettled are therefore **buffered** and merged once fetching settles.
 * 2. **Lifecycle truth is server-authoritative** (R11/F2). The client cannot infer "still
 *    running" from paginated history — a completion off the loaded pages, filtered out, or
 *    for a since-cleared row is simply invisible there. Reconciliation instead asks the
 *    server for its live in-flight set (`GET /active`) and removes any in-flight row the
 *    server no longer lists as active (below the completed-seq boundary, so a request that
 *    started after the snapshot is never expired). A genuinely long-running request survives
 *    because it is genuinely in the set — never expired by a timer or a distance heuristic.
 * 3. **Server identity is explicit** (R11/H0b). Every SSE connection opens with a `hello`
 *    carrying the process run id, and `/active` echoes it. A run-id change means Vessel
 *    restarted: the client's in-flight seqs belong to a dead process (their low watermark
 *    can't be boundary-compared against old high seqs), so it discards the whole map.
 * 4. **Loss detection is ordered, and recovery is coalesced** (R22/F1). The server publishes
 *    SSE ids under a lock, so a gap means real loss rather than a reordering; the client
 *    never rewinds its watermark, and a burst of gaps collapses (debounced, single-flight)
 *    into one recovery instead of a reconciliation storm.
 * 5. **Clearing is an in-band, ordered event** (R23/H0a). The server publishes `cleared`
 *    under the same lock as `completed`, so a row a clear deletes is always seen `completed`
 *    *before* `cleared`. On `cleared` the client purges buffered + listed rows matching the
 *    server's own predicate (all, or `startedAt < beforeTs`); anything received afterwards is
 *    post-clear by construction (covers SQLite id reuse). No generation counter, no id
 *    boundary — those couldn't describe every valid clear/SSE ordering.
 * 6. **In-flight rows obey session scope and nothing else** (D05). `started` carries
 *    `sessionId`; other filters can't apply to a row with no final status/model, so the list
 *    collapses them to a count.
 */

/** How long to wait after the first gap before reconciling, so a burst coalesces into one run. */
const RECONCILE_DEBOUNCE_MS = 150

type ListCache = InfiniteData<RequestListResponse, number | undefined>

export interface LiveHistory {
  /** In-flight requests within the viewed session scope, in arrival order. */
  inFlight: InFlightRequest[]
  connected: boolean
  /** Completions that arrived while a filter was active, so the list is knowingly stale. */
  newSinceFilter: number
  clearNewSinceFilter: () => void
}

export function useLiveHistory({
  scope,
  filters,
  onCompleted,
}: {
  scope: SessionScope | null
  filters: RequestFilters
  /** Fired for every completion, in arrival order, regardless of scope/filters (selection handover). */
  onCompleted?: (row: Summary | null, seq: number) => void
}): LiveHistory {
  const queryClient = useQueryClient()
  const [inFlightMap, setInFlightMap] = useState<Map<number, InFlightRequest>>(new Map())
  const [newSince, setNewSince] = useState<{ signature: string; count: number }>({ signature: '', count: 0 })

  // Read inside SSE callbacks, which are installed once and must not capture stale values.
  // Written in an effect, not during render: these are only ever read asynchronously.
  const scopeRef = useRef(scope)
  const filtersRef = useRef(filters)
  const onCompletedRef = useRef(onCompleted)
  useEffect(() => {
    scopeRef.current = scope
    filtersRef.current = filters
    onCompletedRef.current = onCompleted
  })

  // R10 — completions held while a list fetch is unsettled, drained on the falling edge.
  const pendingRef = useRef<Summary[]>([])
  const listFetching = useIsFetching({ queryKey: REQUESTS_QUERY_ROOT })

  // R11/H0b — the run id the current SSE connection last announced via `hello`. A `/active`
  // response (or a later hello) carrying a different id means a restart, and every in-flight
  // seq we hold is from the dead process.
  const serverRunIdRef = useRef<string | null>(null)

  // Scoped to the view it was counted for, so switching scope/filters resets it without an
  // effect (a setState-in-effect here would cascade a second render on every switch).
  const queryKeySignature = JSON.stringify(requestsQueryKey(scope, filters))
  const newSinceFilter = newSince.signature === queryKeySignature ? newSince.count : 0

  /** Splice one completed row into the current list cache; dedupes, so it is safe to retry. */
  const mergeRow = useCallback(
    (row: Summary) => {
      const currentScope = scopeRef.current
      if (currentScope === null) return
      if (currentScope !== 'all' && row.sessionId !== currentScope) return

      if (filtersActive(filtersRef.current)) {
        // A new row may not match the active filter, and refetching on every completion
        // would defeat the cache — so the list stays put and offers a refresh instead.
        const signature = JSON.stringify(requestsQueryKey(currentScope, filtersRef.current))
        setNewSince((prev) => ({
          signature,
          count: prev.signature === signature ? prev.count + 1 : 1,
        }))
        return
      }

      const key = requestsQueryKey(currentScope, filtersRef.current)
      queryClient.setQueryData<ListCache>(key, (old) => {
        if (!old) return old
        const first = old.pages[0]
        if (first.rows.some((r) => r.id === row.id)) return old
        const pages = [...old.pages]
        pages[0] = { ...first, rows: [row, ...first.rows] }
        return { ...old, pages }
      })
    },
    [queryClient],
  )

  // R10 — drain the buffer once every list fetch has settled. Doing this on the falling
  // edge (and not while fetching) is the whole point: a fetch resolving with a snapshot
  // older than the completion would otherwise overwrite the row back out of the cache.
  // R23/H0a — the buffer was already purged of cleared rows when the `cleared` frame arrived,
  // and everything still here is post-clear, so draining just merges.
  useEffect(() => {
    if (listFetching > 0 || pendingRef.current.length === 0) return
    const buffered = pendingRef.current
    pendingRef.current = []
    for (const row of buffered) mergeRow(row)
  }, [listFetching, mergeRow])

  /**
   * R11/F2 — the authoritative path. Ask the server which requests are genuinely still in
   * flight, drop any in-flight row it no longer lists (and that is old enough to have been
   * accounted for), then refetch history, stats and facets together so completed rows land.
   */
  const reconcile = useCallback(async () => {
    let active
    try {
      active = await api.getActiveRequests()
    } catch {
      return // transient; the next gap or reconnect retries
    }

    if (serverRunIdRef.current !== null && active.serverRunId !== serverRunIdRef.current) {
      // R11/H0b — the active set is from a different Vessel run than our SSE connection knows;
      // our seqs are meaningless against its watermark. Discard the whole in-flight map rather
      // than boundary-comparing across process lifetimes.
      setInFlightMap((prev) => (prev.size === 0 ? prev : new Map()))
    } else {
      const activeSet = new Set(active.activeSeqs)
      setInFlightMap((prev) => {
        let changed = false
        const next = new Map(prev)
        for (const seq of prev.keys()) {
          // Absent from the server's active set and at/below the completed boundary: finished
          // or dropped. A seq above the boundary may just have started after the snapshot.
          if (!activeSet.has(seq) && seq <= active.newestCompletedSeq) {
            next.delete(seq)
            changed = true
          }
        }

        return changed ? next : prev
      })
    }

    await Promise.all([
      queryClient.refetchQueries({ queryKey: REQUESTS_QUERY_ROOT }),
      queryClient.invalidateQueries({ queryKey: ['stats'] }),
      queryClient.invalidateQueries({ queryKey: ['facets'] }),
    ])
  }, [queryClient])

  // R22/F1 — coalesce recovery: a burst of gaps produces one reconciliation, not one per
  // gap. A debounce collapses the burst; a single-flight guard folds gaps that arrive during
  // a run into exactly one follow-up, so overlapping reconciliations never pile up.
  const reconcileTimerRef = useRef<number | null>(null)
  const reconcilingRef = useRef(false)
  const reconcileQueuedRef = useRef(false)
  const scheduleReconcile = useCallback(() => {
    if (reconcileTimerRef.current !== null) return
    reconcileTimerRef.current = window.setTimeout(() => {
      reconcileTimerRef.current = null
      if (reconcilingRef.current) {
        reconcileQueuedRef.current = true
        return
      }

      reconcilingRef.current = true
      void (async () => {
        try {
          do {
            reconcileQueuedRef.current = false
            await reconcile()
          } while (reconcileQueuedRef.current)
        } finally {
          reconcilingRef.current = false
        }
      })()
    }, RECONCILE_DEBOUNCE_MS)
  }, [reconcile])

  useEffect(
    () => () => {
      if (reconcileTimerRef.current !== null) window.clearTimeout(reconcileTimerRef.current)
    },
    [],
  )

  /**
   * R23/H0a — purge the rows a clear removed. Runs when the ordered `cleared` frame arrives:
   * everything cleared has already been seen `completed` (so it is buffered or listed by now),
   * and everything received after is post-clear. Matches the server's own predicate exactly.
   */
  const applyCleared = useCallback(
    (data: ClearedEvent) => {
      const removed = (row: Summary) =>
        data.scope === 'all' ||
        (data.beforeTs !== null && new Date(row.startedAt) < new Date(data.beforeTs))

      if (pendingRef.current.length > 0) {
        pendingRef.current = pendingRef.current.filter((row) => !removed(row))
      }

      for (const [key, cache] of queryClient.getQueriesData<ListCache>({ queryKey: REQUESTS_QUERY_ROOT })) {
        if (!cache) continue
        let changed = false
        const pages = cache.pages.map((page) => {
          const rows = page.rows.filter((r) => !removed(r))
          if (rows.length === page.rows.length) return page
          changed = true
          return { ...page, rows }
        })
        if (changed) queryClient.setQueryData<ListCache>(key, { ...cache, pages })
      }
    },
    [queryClient],
  )

  const { connected } = useEvents({
    onStarted: (data) => {
      setInFlightMap((prev) => {
        const next = new Map(prev)
        next.set(data.seq, { ...data })
        return next
      })
    },
    onRequestReady: (data) => {
      setInFlightMap((prev) => {
        const existing = prev.get(data.seq)
        if (!existing) return prev
        const next = new Map(prev)
        next.set(data.seq, { ...existing, model: data.model })
        return next
      })
    },
    onFirstToken: (data) => {
      setInFlightMap((prev) => {
        const existing = prev.get(data.seq)
        if (!existing) return prev
        const next = new Map(prev)
        next.set(data.seq, { ...existing, ttftMs: data.ttftMs })
        return next
      })
    },
    onCompleted: (data) => {
      setInFlightMap((prev) => {
        if (!prev.has(data.seq)) return prev
        const next = new Map(prev)
        next.delete(data.seq)
        return next
      })

      onCompletedRef.current?.(data.row, data.seq)

      if (!data.row) return

      // A completed row can introduce a tag/model/backend/format the filter bar's cached
      // facets don't know about. Only invalidate entries actually missing something, so
      // ordinary traffic doesn't refetch facets on every completion.
      for (const [key, cached] of queryClient.getQueriesData<{
        backends: string[]
        models: string[]
        formats: string[]
        tags: string[]
      }>({ queryKey: ['facets'] })) {
        if (introducesNewFacet(data.row, cached)) {
          void queryClient.invalidateQueries({ queryKey: key })
        }
      }

      // R10 — a list fetch is in flight, and it may be about to resolve with a snapshot taken
      // before this row existed. Hold the row and merge it after settlement rather than
      // writing into a cache about to be replaced. A `cleared` arriving before the drain will
      // purge it if the clear removed it (R23/H0a).
      if (queryClient.isFetching({ queryKey: REQUESTS_QUERY_ROOT }) > 0) {
        pendingRef.current.push(data.row)
        return
      }

      mergeRow(data.row)
    },
    onCleared: (data) => {
      applyCleared(data)
    },
    onHello: (data) => {
      const prev = serverRunIdRef.current
      serverRunIdRef.current = data.serverRunId
      if (prev !== null && prev !== data.serverRunId) {
        // R11/H0b — Vessel restarted under this reconnecting connection. Every in-flight seq
        // is from the dead process; discard them, then reconcile against the fresh server.
        setInFlightMap((current) => (current.size === 0 ? current : new Map()))
        scheduleReconcile()
      }
    },
    onGap: () => {
      scheduleReconcile()
    },
    onReconnect: () => {
      scheduleReconcile()
    },
  })

  // D05 — in-flight rows are scoped to the viewed session and nothing else.
  const inFlight = Array.from(inFlightMap.values()).filter(
    (item) => scope === 'all' || (scope !== null && item.sessionId === scope),
  )

  return {
    inFlight,
    connected,
    newSinceFilter,
    clearNewSinceFilter: useCallback(() => setNewSince({ signature: '', count: 0 }), []),
  }
}

/** True when `row` has a tag/model/backend/format not already in `cached`. */
function introducesNewFacet(
  row: Summary,
  cached: { backends: string[]; models: string[]; formats: string[]; tags: string[] } | undefined,
): boolean {
  if (!cached) return false
  return (
    !cached.backends.includes(row.backend) ||
    !cached.formats.includes(row.format) ||
    (row.model != null && !cached.models.includes(row.model)) ||
    row.tags.some((t) => !cached.tags.includes(t))
  )
}
