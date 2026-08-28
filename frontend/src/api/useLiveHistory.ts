import { useCallback, useEffect, useRef, useState } from 'react'
import { useIsFetching, useQueryClient, type InfiniteData } from '@tanstack/react-query'
import {
  filtersActive,
  type RequestFilters,
  type RequestListResponse,
  type SessionScope,
  type Summary,
} from './types'
import { REQUESTS_QUERY_ROOT, requestsQueryKey } from './queryKeys'
import { useEvents, type InFlightRequest } from './useEvents'

/**
 * R10/R11/D05 — one reconciliation model for live rows, rather than three independent
 * patches. Three things had to hold together:
 *
 * 1. **A completion must never be lost across a fetch boundary.** The previous code
 *    assumed `invalidateQueries` queues a refetch behind an in-flight one. With this
 *    TanStack Query version an *initial* fetch (no cached data) reuses the existing
 *    promise instead, so a completion arriving during initial load was dropped and never
 *    reappeared without a manual reload. Completions arriving while any list fetch is
 *    unsettled are therefore **buffered** and merged once fetching settles — the merge
 *    dedupes by id, so it is harmless when the fetch's own snapshot already contained the
 *    row.
 * 2. **Lost events must be recoverable.** Subscriber queues drop oldest by design, and
 *    the client only removed an in-flight row on `completed` — so a dropped completion
 *    left a row running forever (the review saw 21 of them, with 53-second timers, after
 *    a 10k burst). The server now stamps every SSE frame with a monotonic publish id, so
 *    a gap is *detectable*; a gap, or a reconnect, triggers authoritative reconciliation:
 *    refetch history + stats + facets together, then drop in-flight entries the refreshed
 *    history accounts for. Entries it does not account for are left alone — a genuinely
 *    long-running request must never be expired by a timer or a distance heuristic.
 * 3. **In-flight rows obey session scope and nothing else** (D05). `started` now carries
 *    `sessionId`, so scoping is accurate rather than guessed. Other filters can't be
 *    applied to a row that has no final status or model yet, so the list collapses them
 *    to a count instead of pretending to filter them.
 */

/** In-flight entries are correlated to stored rows by this, because `seq` is not persisted. */
function identityOf(value: { startedAt: string; method: string; path: string }): string {
  return `${value.startedAt}|${value.method}|${value.path}`
}

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

  const pendingRef = useRef<Summary[]>([])
  const listFetching = useIsFetching({ queryKey: REQUESTS_QUERY_ROOT })

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
      queryClient.setQueryData<InfiniteData<RequestListResponse, number | undefined>>(key, (old) => {
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
  useEffect(() => {
    if (listFetching > 0 || pendingRef.current.length === 0) return
    const buffered = pendingRef.current
    pendingRef.current = []
    for (const row of buffered) {
      mergeRow(row)
    }
  }, [listFetching, mergeRow])

  /**
   * R11 — the authoritative path. Refetch history, stats and facets together (facets were
   * previously never refreshed on reconnect), then drop in-flight entries the refreshed
   * history now accounts for. Anything still unaccounted for stays: it is either genuinely
   * running or will be reconciled by the next refresh.
   */
  const reconcile = useCallback(async () => {
    await Promise.all([
      queryClient.refetchQueries({ queryKey: REQUESTS_QUERY_ROOT }),
      queryClient.invalidateQueries({ queryKey: ['stats'] }),
      queryClient.invalidateQueries({ queryKey: ['facets'] }),
    ])

    const known = new Set<string>()
    for (const [, data] of queryClient.getQueriesData<InfiniteData<RequestListResponse, number | undefined>>({
      queryKey: REQUESTS_QUERY_ROOT,
    })) {
      for (const page of data?.pages ?? []) {
        for (const row of page.rows) {
          known.add(identityOf(row))
        }
      }
    }

    setInFlightMap((prev) => {
      let changed = false
      const next = new Map(prev)
      for (const [seq, item] of prev) {
        if (known.has(identityOf(item))) {
          next.delete(seq)
          changed = true
        }
      }

      return changed ? next : prev
    })
  }, [queryClient])

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

      // R10 — a list fetch is in flight, and it may be about to resolve with a snapshot
      // taken before this row existed. Hold the row and merge it after settlement rather
      // than writing into a cache that is about to be replaced.
      if (queryClient.isFetching({ queryKey: REQUESTS_QUERY_ROOT }) > 0) {
        pendingRef.current.push(data.row)
        return
      }

      mergeRow(data.row)
    },
    onGap: () => {
      void reconcile()
    },
    onReconnect: () => {
      void reconcile()
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
