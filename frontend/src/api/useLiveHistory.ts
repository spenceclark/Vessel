import { useCallback, useEffect, useRef, useState } from 'react'
import { useIsFetching, useQueryClient, type InfiniteData } from '@tanstack/react-query'
import {
  filtersActive,
  type ClearState,
  type CompletedEvent,
  type FirstTokenEvent,
  type RequestFilters,
  type RequestListResponse,
  type RequestReadyEvent,
  type SessionScope,
  type StartedEvent,
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
 * 3. **Server identity is explicit, and evidence has a validity window** (R11/H0b/I0b). Every
 *    SSE connection opens with a `hello` carrying the process run id, and `/active` echoes it.
 *    A run-id change *on the hello* means Vessel restarted: the client's in-flight seqs belong
 *    to a dead process (their low watermark can't be boundary-compared against old high seqs),
 *    so it discards the whole map. A recovery *response* is different: it is applied only if
 *    its own run id and its issuing request's run id both still equal the current run.
 *    Anything else is a response from an obsolete lifetime and is discarded outright — never
 *    read as evidence that the run we are connected to restarted, which would delete the live
 *    requests of the run that is actually running.
 * 4. **Loss detection is ordered, and recovery is coalesced** (R22/F1). The server publishes
 *    SSE ids under a lock, so a gap means real loss rather than a reordering; the client
 *    never rewinds its watermark, and a burst of gaps collapses (debounced, single-flight)
 *    into one recovery instead of a reconciliation storm.
 * 5. **Clearing is versioned, recoverable state — the frame is only the fast path**
 *    (R23/H0a/I0a). The server publishes `cleared` under the same lock as `completed`, so a
 *    row a clear deletes is always seen `completed` *before* `cleared`, and it reports the
 *    same versioned predicate on `/active`. The client purges buffered + listed rows by that
 *    predicate on arrival, re-applies it once every list fetch outstanding at that moment has
 *    settled (a pending REST snapshot can be older than the clear — TanStack reuses an
 *    unsettled initial fetch rather than replacing it on invalidation), and learns a clear it
 *    never saw from the version on `/active` during recovery. Correctness therefore never
 *    depends on a `cleared` frame surviving a deliberately lossy feed.
 * 6. **In-flight rows obey session scope and nothing else** (D05). `started` carries
 *    `sessionId`; other filters can't apply to a row with no final status/model, so the list
 *    collapses them to a count.
 * 7. **Event application is coalesced** (I0c). A frame is queued, not applied: every ~100 ms
 *    the whole window lands as one state update and one cache write. Profiling a live 10k
 *    burst with the tab connected showed the per-frame version stalling the main thread for
 *    10.3 s in a single task while the JS heap climbed from 76 MB to 3.1 GB — a throughput
 *    shape, independent of ordering correctness, which the coalesced version does not have.
 */

/** How long to wait after the first gap before reconciling, so a burst coalesces into one run. */
const RECONCILE_DEBOUNCE_MS = 150

/**
 * I0c — the SSE event coalescing window (~10 Hz). Frames are queued as they arrive and applied
 * together, instead of one React state update per frame. Imperceptible for a monitoring UI —
 * in-flight rows already animate off their own shared 250 ms tick — and the difference between
 * a live tab surviving a burst and one that does not; see the flush below.
 */
const EVENT_FLUSH_MS = 100

type ListCache = InfiniteData<RequestListResponse, number | undefined>

/** One queued SSE frame, tagged so the flush can apply a window's worth in arrival order. */
type QueuedEvent =
  | { kind: 'started'; data: StartedEvent }
  | { kind: 'request_ready'; data: RequestReadyEvent }
  | { kind: 'first_token'; data: FirstTokenEvent }
  | { kind: 'completed'; data: CompletedEvent }
  | { kind: 'cleared'; data: ClearState }

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

  // R11/H0b — the run id the current SSE connection last announced via `hello`. This is the
  // *only* signal of a restart (I0b): a mismatching `/active` response is a stale response, not
  // evidence about the run we are connected to.
  const serverRunIdRef = useRef<string | null>(null)

  // I0c — frames arrive far faster than React can usefully render them (a 10k burst runs at
  // ~1.3k frames/s). They are queued here and applied on a ~10 Hz window by `flushEvents`.
  const eventQueueRef = useRef<QueuedEvent[]>([])
  const flushTimerRef = useRef<number | null>(null)

  // I0a/R23 — the latest clear this client knows about (from the in-band frame or from
  // recovery), the ids it knows to be post-clear, and whether a list fetch that could predate
  // that clear may still be outstanding. See `learnClear` / `purgeCleared`.
  const clearRef = useRef<ClearState | null>(null)
  const postClearIdsRef = useRef<Set<number>>(new Set())
  const clearPendingSettleRef = useRef(false)

  // Scoped to the view it was counted for, so switching scope/filters resets it without an
  // effect (a setState-in-effect here would cascade a second render on every switch).
  const queryKeySignature = JSON.stringify(requestsQueryKey(scope, filters))
  const newSinceFilter = newSince.signature === queryKeySignature ? newSince.count : 0

  /**
   * Splice completed rows into the current list cache in one write; dedupes, so it is safe to
   * retry. I0c — deliberately a *batch*: one cache write per flush rather than per completion.
   * Each write clones the page array and notifies every observer, so at burst rates the
   * per-row version was the dominant allocation and re-render cost.
   */
  const mergeRows = useCallback(
    (rows: Summary[]) => {
      const currentScope = scopeRef.current
      if (currentScope === null) return
      const scoped = currentScope === 'all' ? rows : rows.filter((r) => r.sessionId === currentScope)
      if (scoped.length === 0) return

      if (filtersActive(filtersRef.current)) {
        // A new row may not match the active filter, and refetching on every completion
        // would defeat the cache — so the list stays put and offers a refresh instead.
        const signature = JSON.stringify(requestsQueryKey(currentScope, filtersRef.current))
        setNewSince((prev) => ({
          signature,
          count: (prev.signature === signature ? prev.count : 0) + scoped.length,
        }))
        return
      }

      const key = requestsQueryKey(currentScope, filtersRef.current)
      queryClient.setQueryData<ListCache>(key, (old) => {
        if (!old) return old
        const first = old.pages[0]
        const seen = new Set(first.rows.map((r) => r.id))
        const fresh: Summary[] = []
        for (const row of scoped) {
          if (seen.has(row.id)) continue
          seen.add(row.id)
          fresh.push(row)
        }

        if (fresh.length === 0) return old
        const pages = [...old.pages]
        // Newest first: `scoped` is in completion order, so the last arrival heads the page.
        pages[0] = { ...first, rows: [...fresh.reverse(), ...first.rows] }
        return { ...old, pages }
      })
    },
    [queryClient],
  )

  /**
   * I0a/R23 — is `row` one the recorded clear deleted? The server's own predicate: for a
   * clear-before, the `startedAt` cutoff it deleted by; for a clear-all, the id boundary (every
   * row that existed then is at or below it).
   *
   * `postClearIds` is the exemption that makes re-application safe. A clear-all empties the
   * table, so SQLite hands the *next* rows ids starting from 1 again — squarely inside the
   * boundary. Any row the client learned from a completion published after the clear is
   * post-clear by construction (the wire order guarantees it), so it is recorded and never
   * purged, however its id compares.
   */
  const clearedRow = useCallback((clear: ClearState, row: Summary) => {
    if (postClearIdsRef.current.has(row.id)) return false
    return clear.scope === 'all'
      ? row.id <= clear.boundaryId
      : clear.beforeTs !== null && new Date(row.startedAt) < new Date(clear.beforeTs)
  }, [])

  /** Applies a recorded clear to the completion buffer and every cached list page. Idempotent. */
  const purgeCleared = useCallback(
    (clear: ClearState) => {
      if (pendingRef.current.length > 0) {
        pendingRef.current = pendingRef.current.filter((row) => !clearedRow(clear, row))
      }

      for (const [key, cache] of queryClient.getQueriesData<ListCache>({ queryKey: REQUESTS_QUERY_ROOT })) {
        if (!cache) continue
        let changed = false
        const pages = cache.pages.map((page) => {
          const rows = page.rows.filter((r) => !clearedRow(clear, r))
          if (rows.length === page.rows.length) return page
          changed = true
          return { ...page, rows }
        })
        if (changed) queryClient.setQueryData<ListCache>(key, { ...cache, pages })
      }
    },
    [clearedRow, queryClient],
  )

  /**
   * I0a/R23 — record a clear (from the in-band frame or from `/active` during recovery) and
   * purge by it. Older or repeated versions are ignored, so the two paths are safe to overlap.
   *
   * The re-application is armed only when a list fetch is outstanding right now: those are
   * exactly the fetches whose server-side snapshot can predate this clear, and they resolve
   * into the cache *after* this purge. Once list fetching next reaches zero they have all
   * settled, and every later response comes from a post-clear database — so the predicate is
   * applied one final time and retired, rather than left standing to fight reused ids forever.
   */
  const learnClear = useCallback(
    (clear: ClearState) => {
      if (clearRef.current !== null && clear.version <= clearRef.current.version) return
      clearRef.current = clear
      postClearIdsRef.current = new Set()
      clearPendingSettleRef.current = queryClient.isFetching({ queryKey: REQUESTS_QUERY_ROOT }) > 0
      purgeCleared(clear)
    },
    [purgeCleared, queryClient],
  )

  // R10 — drain the buffer once every list fetch has settled. Doing this on the falling
  // edge (and not while fetching) is the whole point: a fetch resolving with a snapshot
  // older than the completion would otherwise overwrite the row back out of the cache.
  // I0a — and it is the moment a pre-clear snapshot has finished landing in the cache, so the
  // clear predicate is re-applied here, before the buffer drains into it.
  useEffect(() => {
    if (listFetching > 0) return

    const clear = clearRef.current
    if (clear !== null && clearPendingSettleRef.current) {
      clearPendingSettleRef.current = false
      purgeCleared(clear)
    }

    if (pendingRef.current.length === 0) return
    const buffered = pendingRef.current
    pendingRef.current = []
    mergeRows(buffered)
  }, [listFetching, mergeRows, purgeCleared])

  /**
   * R11/F2 — the authoritative path. Ask the server which requests are genuinely still in
   * flight, drop any in-flight row it no longer lists (and that is old enough to have been
   * accounted for), then refetch history, stats and facets together so completed rows land.
   */
  const reconcile = useCallback(async () => {
    // I0b(2) — the run this recovery is *about*. A response is evidence only while both this
    // and its own run id still match the connection's current run.
    const runAtIssue = serverRunIdRef.current

    let active
    try {
      active = await api.getActiveRequests()
    } catch {
      return // transient; the next gap or reconnect retries
    }

    const currentRun = serverRunIdRef.current
    const applies =
      currentRun === runAtIssue && (currentRun === null || active.serverRunId === currentRun)

    if (applies) {
      const activeSet = new Set(active.activeSeqs)
      setInFlightMap((prev) => {
        let changed = false
        const next = new Map(prev)
        for (const seq of prev.keys()) {
          // Absent from the server's active set and at/below the completed boundary: finished
          // or dropped. A seq above the boundary may just have started after the snapshot —
          // and since I0b(1) allocates a seq only as it registers it, a seq missing from a
          // coherent snapshot was necessarily allocated after that snapshot, hence above its
          // boundary. So "absent and at/below the boundary" now means finished, full stop.
          if (!activeSet.has(seq) && seq <= active.newestCompletedSeq) {
            next.delete(seq)
            changed = true
          }
        }

        return changed ? next : prev
      })

      // I0a — the deletion state we may have missed: this is how a clear whose `cleared` frame
      // the bounded queue dropped still reaches the cache and the completion buffer.
      if (active.clear !== null) {
        learnClear(active.clear)
      }
    }
    // Otherwise the response describes a Vessel lifetime we are no longer connected to (or one
    // that changed under the request). It is discarded, *not* treated as a restart: expiring
    // the current run's live requests on obsolete evidence is exactly the R11 failure. Only a
    // `hello` changes the run, and it schedules its own reconciliation. The refetches below
    // still run — history/stats/facets are not run-scoped and are stale either way.

    await Promise.all([
      queryClient.refetchQueries({ queryKey: REQUESTS_QUERY_ROOT }),
      queryClient.invalidateQueries({ queryKey: ['stats'] }),
      queryClient.invalidateQueries({ queryKey: ['facets'] }),
    ])
  }, [learnClear, queryClient])

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
   * I0c — apply one coalescing window's worth of events. Two passes over the same queue, in
   * arrival order: one functional state update for every lifecycle change, then the cache and
   * buffer effects. Order within the window is preserved exactly, which is what lets a
   * `cleared` still divide the completions it deletes from the ones it does not.
   */
  const flushEvents = useCallback(() => {
    const queued = eventQueueRef.current
    if (queued.length === 0) return
    eventQueueRef.current = []

    // One state update for the whole window. Previously every frame produced its own Map
    // clone and its own render pass; at ~1.3k frames/s that was the burst's dominant cost.
    setInFlightMap((prev) => {
      let next: Map<number, InFlightRequest> | null = null
      const current = () => next ?? prev
      const edit = () => (next ??= new Map(prev))
      for (const event of queued) {
        switch (event.kind) {
          case 'started':
            edit().set(event.data.seq, { ...event.data })
            break
          case 'request_ready': {
            const existing = current().get(event.data.seq)
            if (existing) edit().set(event.data.seq, { ...existing, model: event.data.model })
            break
          }

          case 'first_token': {
            const existing = current().get(event.data.seq)
            if (existing) edit().set(event.data.seq, { ...existing, ttftMs: event.data.ttftMs })
            break
          }

          case 'completed':
            if (current().has(event.data.seq)) edit().delete(event.data.seq)
            break
        }
      }

      return next ?? prev
    })

    const toMerge: Summary[] = []
    for (const event of queued) {
      if (event.kind === 'cleared') {
        learnClear(event.data)
        // Rows completed earlier in *this* window are not in the cache yet, so the purge above
        // cannot see them: apply the predicate to them here, keeping the wire's ordering.
        const clear = clearRef.current
        if (clear !== null && toMerge.length > 0) {
          const kept = toMerge.filter((row) => !clearedRow(clear, row))
          toMerge.length = 0
          toMerge.push(...kept)
        }

        continue
      }

      if (event.kind !== 'completed') continue
      const { row, seq } = event.data
      onCompletedRef.current?.(row, seq)
      if (!row) continue

      // A completed row can introduce a tag/model/backend/format the filter bar's cached
      // facets don't know about. Only invalidate entries actually missing something, so
      // ordinary traffic doesn't refetch facets on every completion.
      for (const [key, cached] of queryClient.getQueriesData<{
        backends: string[]
        models: string[]
        formats: string[]
        tags: string[]
      }>({ queryKey: ['facets'] })) {
        if (introducesNewFacet(row, cached)) {
          void queryClient.invalidateQueries({ queryKey: key })
        }
      }

      // I0a — this completion is published after any clear we already know about (the wire
      // orders a deleted row's `completed` before `cleared`), so the row is post-clear however
      // its id compares to the boundary. Record it, so re-applying the predicate at settlement
      // never purges a live row that merely reuses a cleared id.
      if (clearRef.current !== null && clearedRow(clearRef.current, row)) {
        postClearIdsRef.current.add(row.id)
      }

      toMerge.push(row)
    }

    if (toMerge.length === 0) return

    // R10 — a list fetch is in flight, and it may be about to resolve with a snapshot taken
    // before these rows existed. Hold them and merge after settlement rather than writing into
    // a cache about to be replaced. A `cleared` arriving before the drain will purge whatever
    // the clear removed (R23/H0a).
    if (queryClient.isFetching({ queryKey: REQUESTS_QUERY_ROOT }) > 0) {
      pendingRef.current.push(...toMerge)
      return
    }

    mergeRows(toMerge)
  }, [clearedRow, learnClear, mergeRows, queryClient])

  /** Queue one event, arming the coalescing window if it is not already running. */
  const enqueueEvent = useCallback(
    (event: QueuedEvent) => {
      eventQueueRef.current.push(event)
      if (flushTimerRef.current !== null) return
      flushTimerRef.current = window.setTimeout(() => {
        flushTimerRef.current = null
        flushEvents()
      }, EVENT_FLUSH_MS)
    },
    [flushEvents],
  )

  useEffect(
    () => () => {
      if (flushTimerRef.current !== null) window.clearTimeout(flushTimerRef.current)
    },
    [],
  )

  const { connected } = useEvents({
    onStarted: (data) => enqueueEvent({ kind: 'started', data }),
    onRequestReady: (data) => enqueueEvent({ kind: 'request_ready', data }),
    onFirstToken: (data) => enqueueEvent({ kind: 'first_token', data }),
    onCompleted: (data) => enqueueEvent({ kind: 'completed', data }),
    onCleared: (data) => enqueueEvent({ kind: 'cleared', data }),
    onHello: (data) => {
      const prev = serverRunIdRef.current
      serverRunIdRef.current = data.serverRunId
      if (prev !== null && prev !== data.serverRunId) {
        // R11/H0b — Vessel restarted under this reconnecting connection. Every in-flight seq
        // is from the dead process; discard them, then reconcile against the fresh server.
        // I0a — clear versions are per run too (they restart with the process), so the recorded
        // predicate is dropped rather than compared against the new run's versions.
        setInFlightMap((current) => (current.size === 0 ? current : new Map()))
        clearRef.current = null
        postClearIdsRef.current = new Set()
        clearPendingSettleRef.current = false
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
