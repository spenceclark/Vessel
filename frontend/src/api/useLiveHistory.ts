import { useCallback, useEffect, useRef, useState } from 'react'
import { useQueryClient, type InfiniteData } from '@tanstack/react-query'
import {
  filtersActive,
  type ActiveDescriptor,
  type CompletedEvent,
  type ClearedEvent,
  type FirstTokenEvent,
  type RequestFilters,
  type RequestListResponse,
  type RequestReadyEvent,
  type SessionScope,
  type StartedEvent,
  type Summary,
} from './types'
import { api } from './client'
import { AGGREGATE_QUERY_ROOT, REQUESTS_QUERY_ROOT, SERIES_QUERY_ROOT, requestsQueryKey } from './queryKeys'
import { useEvents, type InFlightRequest } from './useEvents'

/**
 * R10/R11/R22/R23/D05 — one reconciliation model for live rows, rather than independent
 * patches. **J0 (approved 2026-08-29) replaced the accreted merge rules with two operations:
 * snapshot recovery and ordered replay.** Everything below follows from that:
 *
 * 1. **The SSE event id is the single log position.** The server allocates it under the
 *    publish lock, so every lifecycle change — including `cleared` — has one position that
 *    orders it against every other, and `GET /active` reports the position its in-flight set
 *    is true as of. The client never invents an ordering of its own.
 * 2. **Recovery is wholesale replacement** (rule 2). On reconnect, gap, or run change: fetch
 *    the snapshot, then discard the applied map, the *entire* completion buffer, and every
 *    held event at or below `logPosition`; rebuild the in-flight rows from the snapshot's
 *    descriptors; refetch history/stats/facets. Discarding is safe because those events are,
 *    by construction, already reflected in the snapshot and in the database the refetch reads:
 *    an event the client received before it issued the request was published before the server
 *    took the snapshot, so its id is at or below that position. Events above it replay on top.
 *    **K0b** — the snapshot describes each active request rather than naming it, so a request
 *    whose `started` frame the feed dropped is *displayed* after recovery, not merely known
 *    about. The intersection with locally-known starts that stood here is gone.
 * 3. **Between recoveries, ordered replay only** (rule 3). Frames apply strictly in id order;
 *    `cleared` drops the cached rows and buffer *at its position* and schedules a refetch;
 *    #41's session deletion carries an exact session-id scope, while all/before clear all.
 *    A detected gap goes to rule 2 — never to ad-hoc reasoning about what was missed.
 * 4. **REST reads are authoritative** (rule 4). Apart from #41's exact session-id predicate, no
 *    boundary, no provenance set: nothing the client holds ever deletes a row a fetch
 *    returned. A clear or recovery always starts a *new* fetch afterwards, and the
 *    last-started fetch wins. **K0a** — "new" has to be enforced, not assumed: TanStack v5
 *    hands back the pending promise of an *initial* fetch instead of starting a second one,
 *    which silently turned this rule into "first fetch wins" and let a pre-clear snapshot
 *    become authoritative. The trigger therefore cancels the outstanding read first (see
 *    `refetchAuthoritative`). The accepted trade, stated in the contract: a stale pre-clear
 *    fetch may show briefly until its superseding refetch settles. Settled state converges.
 * 5. **Server identity gates all of it** (R11/H0b/I0b). Every SSE connection opens with a
 *    `hello` carrying the process run id; a change on the *hello* means Vessel restarted, and
 *    seqs and log positions alike belong to a dead process, so map, buffer, queue and floor
 *    are all dropped. A recovery *response* from another run is discarded as evidence — never
 *    read as a restart, which would expire the live requests of the run actually running.
 * 6. **In-flight rows obey session scope and nothing else** (D05). `started` carries
 *    `sessionId`; other filters can't apply to a row with no final status/model, so the list
 *    collapses them to a count.
 * 7. **Event application is coalesced** (I0c, kept). A frame is queued, not applied: every
 *    ~100 ms the whole window lands as one state update and one cache write. Profiling a live
 *    10k burst with the tab connected showed the per-frame version stalling the main thread
 *    for 10.3 s in a single task while the JS heap climbed from 76 MB to 3.1 GB. Under J0 the
 *    queue is no longer a hazard to reconcile against — it *is* the replay log.
 *
 * What J0 deleted, and why it cannot come back: the versioned clear predicate, its
 * re-application at fetch settlement, the post-clear id exemption and the completed-seq
 * boundary. Each was a client-side rule for deciding which rows a *past* server operation had
 * deleted, and the round-five review broke all of them at once — a queued completion
 * misclassified as post-clear, a valid row purged for reusing a cleared id, and an earlier
 * missed clear erased by a later narrower one. A position needs none of those decisions.
 */

/** How long to wait after the first gap before recovering, so a burst coalesces into one run. */
const RECONCILE_DEBOUNCE_MS = 150

/**
 * I0c — the SSE event coalescing window (~10 Hz). Frames are queued as they arrive and applied
 * together, instead of one React state update per frame. Imperceptible for a monitoring UI —
 * in-flight rows already animate off their own shared 250 ms tick — and the difference between
 * a live tab surviving a burst and one that does not; see the flush below.
 */
const EVENT_FLUSH_MS = 100

type ListCache = InfiniteData<RequestListResponse, number | undefined>

/**
 * One received SSE frame, tagged with its log position so recovery can order the client's
 * pending work against a server snapshot (J0). `id` is the frame's SSE `id:`; a frame that
 * somehow carried no usable id is queued as `Infinity` — "no known position" — so it is
 * applied rather than silently discarded by a position comparison it cannot participate in.
 */
type QueuedEvent = { id: number } & (
  | { kind: 'started'; data: StartedEvent }
  | { kind: 'request_ready'; data: RequestReadyEvent }
  | { kind: 'first_token'; data: FirstTokenEvent }
  | { kind: 'completed'; data: CompletedEvent }
  | { kind: 'cleared'; data: ClearedEvent }
)

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
  sessionName = null,
  filters,
  onCompleted,
}: {
  scope: SessionScope | null
  sessionName?: string | null
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

  // R10 — completions held while a list fetch is unsettled, drained once one settles.
  const pendingRef = useRef<Summary[]>([])

  // R11/H0b — the run id the current SSE connection last announced via `hello`. This is the
  // *only* signal of a restart (I0b): a mismatching `/active` response is a stale response, not
  // evidence about the run we are connected to.
  const serverRunIdRef = useRef<string | null>(null)

  // I0c/J0 — frames arrive far faster than React can usefully render them (a 10k burst runs at
  // ~1.3k frames/s). They are queued here in arrival (= id) order and applied on a ~10 Hz
  // window by `flushEvents`. Under J0 this queue is also half of the replay log: recovery
  // prunes it by log position, and `recordingRef` below covers the other half — frames a
  // window already applied while the snapshot request was in flight.
  const eventQueueRef = useRef<QueuedEvent[]>([])
  const flushTimerRef = useRef<number | null>(null)

  // J0 — while a recovery is in flight, every arriving frame is also recorded here. Those are
  // exactly the frames a snapshot cannot be assumed to account for (anything received *before*
  // the request went out was published before the snapshot was taken, so the snapshot covers
  // it), and the ones above the returned position have to survive wholesale replacement even
  // if the coalescing window applied them while the request was in flight. Recording rather
  // than suspending the flush keeps the UI live while `/active` is slow. See `reconcile`.
  const recordingRef = useRef<QueuedEvent[] | null>(null)

  // J0 — the newest accepted snapshot's `logPosition`. Frames at or below it are already
  // reflected in that snapshot and in the database its refetch read, so they are dropped
  // rather than applied. Recovery prunes the queue by this; the check at flush time covers the
  // same frames still in transit when the snapshot was taken. Reset on a run change: positions
  // restart with the process.
  const logFloorRef = useRef(0)

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
   * J0 rule 3 — a `cleared` frame means "history was deleted at this position". All/before
   * clears drop every cached row without re-deriving their SQL predicate; #41's session clear
   * removes only rows carrying its exact session id. The refetch rule 4 schedules remains the
   * authoritative post-clear view.
   */
  const purgeListCaches = useCallback((sessionId?: number) => {
    for (const [key, cache] of queryClient.getQueriesData<ListCache>({ queryKey: REQUESTS_QUERY_ROOT })) {
      if (!cache) continue
      if (cache.pages.every((page) => page.rows.every((row) => sessionId !== undefined && row.sessionId !== sessionId))) continue
      queryClient.setQueryData<ListCache>(key, {
        ...cache,
        pages: cache.pages.map((page) => ({
          ...page,
          rows: sessionId === undefined ? [] : page.rows.filter((row) => row.sessionId !== sessionId),
        })),
      })
    }
  }, [queryClient])

  /**
   * J0 rule 4 / K0a — the authoritative read: cancel, then refetch.
   *
   * `refetchQueries` alone does not guarantee a new fetch. When the matching query's *initial*
   * fetch is still pending with no cached data, TanStack v5 returns that in-flight retryer's
   * promise instead of starting a second request — so the trigger waited for the very snapshot
   * it was meant to supersede, and a pre-clear response landed as the authoritative one. That
   * is the whole of round six's §2.1, under both a received `cleared` frame and a clear learned
   * through recovery.
   *
   * Cancelling first settles that request as discarded: its response can never be written to
   * the cache, and the refetch that follows is genuinely distinct. Cancellation reaches the
   * network too, because the list query passes TanStack's `signal` to `fetch` (`client.ts`).
   * Note what this does *not* do: no row is inspected, filtered or deleted by id or timestamp —
   * J0 rule 4's no-client-filtering stance is exactly what makes cancellation the right lever.
   */
  const refetchAuthoritative = useCallback(async () => {
    await queryClient.cancelQueries({ queryKey: REQUESTS_QUERY_ROOT })
    await Promise.all([
      queryClient.refetchQueries({ queryKey: REQUESTS_QUERY_ROOT }),
      queryClient.invalidateQueries({ queryKey: ['stats'] }),
      queryClient.invalidateQueries({ queryKey: ['facets'] }),
      // Phase 7 D14 — chart roots are aggregates over the same rows; a clear invalidates
      // them alongside stats so an open Reports view cannot keep pre-clear curves.
      queryClient.invalidateQueries({ queryKey: SERIES_QUERY_ROOT }),
      queryClient.invalidateQueries({ queryKey: AGGREGATE_QUERY_ROOT }),
    ])
  }, [queryClient])

  // R10 — drain the buffer once every list fetch has settled. Waiting for that (rather than
  // merging while one is in flight) is the whole point: a fetch resolving with a snapshot
  // older than the completion would otherwise overwrite the row back out of the cache.
  //
  // Driven off the query cache itself, not off a rendered `useIsFetching` value. A fetch that
  // starts and settles inside one React batch never renders an intermediate "fetching" value,
  // so an effect keyed on that count sees 0 → 0, does not re-run, and strands the buffer until
  // some unrelated fetch happens to move it — losing a completion that arrived alongside a
  // clear's own refetch. The cache notifies on every state change, batched or not.
  useEffect(() => {
    const drain = () => {
      if (pendingRef.current.length === 0) return
      if (queryClient.isFetching({ queryKey: REQUESTS_QUERY_ROOT }) > 0) return
      const buffered = pendingRef.current
      pendingRef.current = [] // before the write: mergeRows notifies, and this runs re-entrantly
      mergeRows(buffered)
    }

    drain()
    return queryClient.getQueryCache().subscribe(drain)
  }, [mergeRows, queryClient])

  /**
   * I0c/J0 — apply one coalescing window's worth of events. Two passes over the same queue, in
   * arrival (= id) order: one functional state update for every lifecycle change, then the
   * cache and buffer effects. Order within the window is preserved exactly, which is what lets
   * a `cleared` still divide the completions it deletes from the ones it does not.
   */
  const flushEvents = useCallback(() => {
    const queued = eventQueueRef.current
    if (queued.length === 0) return
    eventQueueRef.current = []

    // Frames at or below the last accepted snapshot are already reflected in it (and in the
    // database its refetch read). Recovery prunes the queue; this catches the ones that were
    // still in transit at that moment and arrived afterwards.
    const events = logFloorRef.current === 0 ? queued : queued.filter((e) => e.id > logFloorRef.current)
    if (events.length === 0) return

    // One state update for the whole window. Previously every frame produced its own Map
    // clone and its own render pass; at ~1.3k frames/s that was the burst's dominant cost.
    setInFlightMap((prev) => applyLifecycle(prev, events))

    const toMerge: Summary[] = []
    let cleared = false
    for (const event of events) {
      if (event.kind === 'cleared') {
        // J0 rule 3 — everything known at this position goes: the cached rows, the completion
        // buffer, and the rows completed earlier in *this* window (which are not in the cache
        // yet, so the purge above cannot see them). No row is inspected; the refetch below
        // brings back whatever survived.
        const deletedSessionId = event.data.sessionId
        if (deletedSessionId !== undefined) {
          void queryClient.invalidateQueries({ queryKey: ['sessions'] })
        }
        pendingRef.current = deletedSessionId === undefined
          ? []
          : pendingRef.current.filter((row) => row.sessionId !== deletedSessionId)
        if (deletedSessionId === undefined) {
          toMerge.length = 0
        } else {
          for (let i = toMerge.length - 1; i >= 0; i--) {
            if (toMerge[i].sessionId === deletedSessionId) toMerge.splice(i, 1)
          }
        }
        purgeListCaches(deletedSessionId)
        cleared = true
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

      toMerge.push(row)
    }

    // J0 rule 4 — a fetch that starts after the clear, so the last-started fetch is one that
    // read a post-clear database. Any pre-clear fetch still outstanding may land first; it is
    // superseded, which is the contract's stated transient-display trade.
    if (cleared) void refetchAuthoritative()

    if (toMerge.length === 0) return

    // R10 — a list fetch is in flight, and it may be about to resolve with a snapshot taken
    // before these rows existed. Hold them and merge after settlement rather than writing into
    // a cache about to be replaced.
    //
    // K0a — `cleared` counts as "in flight" even though nothing is fetching yet: the clear's
    // own authoritative read starts after an await (it cancels first), so `isFetching` cannot
    // see it here. Merging now would write these post-clear rows into a cache that read is
    // about to replace with a snapshot taken before they existed — and their completions are
    // exactly the rows the buffer exists to protect.
    if (cleared || queryClient.isFetching({ queryKey: REQUESTS_QUERY_ROOT }) > 0) {
      pendingRef.current.push(...toMerge)
      return
    }

    mergeRows(toMerge)
  }, [mergeRows, purgeListCaches, queryClient, refetchAuthoritative])

  /** Arm the coalescing window if it is not already running and there is something to apply. */
  const scheduleFlush = useCallback(() => {
    if (eventQueueRef.current.length === 0) return
    if (flushTimerRef.current !== null) return
    flushTimerRef.current = window.setTimeout(() => {
      flushTimerRef.current = null
      flushEvents()
    }, EVENT_FLUSH_MS)
  }, [flushEvents])

  /** Queue one frame with its log position, arming the coalescing window if needed. */
  const enqueueEvent = useCallback(
    (event: QueuedEvent) => {
      eventQueueRef.current.push(event)
      recordingRef.current?.push(event)
      scheduleFlush()
    },
    [scheduleFlush],
  )

  useEffect(
    () => () => {
      if (flushTimerRef.current !== null) window.clearTimeout(flushTimerRef.current)
    },
    [],
  )

  /**
   * J0 rule 2 — recovery, the only operation that reasons about anything other than frame
   * order. It replaces state rather than editing it: the server's in-flight set becomes the
   * client's, and everything the client was holding at or below the snapshot's position is
   * discarded as already-accounted-for.
   */
  const reconcile = useCallback(async () => {
    // I0b(2) — the run this recovery is *about*. A response is evidence only while both this
    // and its own run id still match the connection's current run.
    const runAtIssue = serverRunIdRef.current

    // Start recording *before* the request goes out. That single ordering is what makes the
    // discard sound: everything received earlier was published before the server took the
    // snapshot, so the snapshot accounts for it and it needs no replay; everything from here
    // on might not be, so it is kept and ordered against the returned position.
    recordingRef.current = []

    let active
    try {
      active = await api.getActiveRequests()
    } catch {
      recordingRef.current = null
      return // transient; the next gap or reconnect retries
    }

    const recorded = recordingRef.current ?? []
    recordingRef.current = null

    const currentRun = serverRunIdRef.current
    const applies =
      currentRun === runAtIssue && (currentRun === null || active.serverRunId === currentRun)

    if (applies) {
      const position = active.logPosition
      logFloorRef.current = Math.max(logFloorRef.current, position)
      // The whole buffer goes: `completed` is published after the row is inserted, so every
      // buffered row is in the database the refetch below reads.
      pendingRef.current = []
      eventQueueRef.current = eventQueueRef.current.filter((e) => e.id > position)

      // Frames published after the snapshot are not described by it, whether they are still
      // queued or were applied by a coalescing window while the request was in flight. They
      // replay, in order, on top of the replacement.
      const replay = recorded.filter((e) => e.id > position)

      setInFlightMap((prev) => {
        // K0b — in-flight := the server's set, rebuilt from its descriptors. Nothing is
        // intersected with what this client happens to have seen: a request whose `started`
        // frame the bounded queue dropped is displayed from the snapshot alone, which is the
        // difference between knowing a request is running and monitoring it.
        const base = new Map<number, InFlightRequest>()
        for (const descriptor of active.active) {
          const known = prev.get(descriptor.seq)
          const row = toInFlight(descriptor)
          // Reuse the existing object when nothing about the row changed, so an unremarkable
          // recovery does not rerender every live row.
          base.set(descriptor.seq, known && sameInFlight(known, row) ? known : row)
        }

        const rebuilt = applyLifecycle(base, replay)
        const unchanged =
          prev.size === rebuilt.size && [...rebuilt].every(([seq, item]) => prev.get(seq) === item)
        return unchanged ? prev : rebuilt
      })
    }
    // Otherwise the response describes a Vessel lifetime we are no longer connected to (or one
    // that changed under the request). It is discarded, *not* treated as a restart: expiring
    // the current run's live requests on obsolete evidence is exactly the R11 failure. Only a
    // `hello` changes the run, and it schedules its own recovery. The refetches below still
    // run — history/stats/facets are not run-scoped and are stale either way.

    await refetchAuthoritative()
  }, [refetchAuthoritative])

  // R22/F1 — coalesce recovery: a burst of gaps produces one recovery, not one per gap. A
  // debounce collapses the burst; a single-flight guard folds gaps that arrive during a run
  // into exactly one follow-up, so overlapping recoveries never pile up.
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

  const { connected } = useEvents({
    onStarted: (data, id) => enqueueEvent({ kind: 'started', data, id }),
    onRequestReady: (data, id) => enqueueEvent({ kind: 'request_ready', data, id }),
    onFirstToken: (data, id) => enqueueEvent({ kind: 'first_token', data, id }),
    onCompleted: (data, id) => enqueueEvent({ kind: 'completed', data, id }),
    onCleared: (data, id) => enqueueEvent({ kind: 'cleared', data, id }),
    onHello: (data) => {
      const prev = serverRunIdRef.current
      serverRunIdRef.current = data.serverRunId
      if (prev !== null && prev !== data.serverRunId) {
        // R11/H0b — Vessel restarted under this reconnecting connection. Every in-flight seq
        // is from the dead process, and so is every queued frame id and the position floor
        // derived from them (J0: log positions restart with the process). Discard all of it,
        // then recover against the fresh server.
        setInFlightMap((current) => (current.size === 0 ? current : new Map()))
        eventQueueRef.current = []
        recordingRef.current = recordingRef.current === null ? null : []
        pendingRef.current = []
        logFloorRef.current = 0
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

  // D05/#29 — headerless in-flight rows match by id; named rows match the selected
  // marker's exact name. A new name has no picker entry until its first insert, so its
  // first request remains visible only in All while in flight.
  const inFlight = Array.from(inFlightMap.values()).filter(
    (item) => scope === 'all'
      || (scope !== null && (item.sessionId === scope || (sessionName !== null && item.sessionName === sessionName))),
  )

  return {
    inFlight,
    connected,
    newSinceFilter,
    clearNewSinceFilter: useCallback(() => setNewSince({ signature: '', count: 0 }), []),
  }
}

/**
 * K0b/R27 — one recovery descriptor as an in-flight row. `ttftMs`, like `model`, now comes
 * straight from the descriptor: the server records it in the same locked descriptor under the
 * same lock it publishes `first_token` from (mirroring how `model` is recorded from
 * `request_ready`), so a `first_token` frame a bounded subscriber queue dropped is still
 * recoverable from the snapshot rather than permanently lost to that client.
 */
function toInFlight(descriptor: ActiveDescriptor): InFlightRequest {
  return {
    seq: descriptor.seq,
    startedAt: descriptor.startedAt,
    sessionId: descriptor.sessionId,
    sessionName: descriptor.sessionName,
    method: descriptor.method,
    path: descriptor.path,
    backend: descriptor.backend,
    tags: descriptor.tags,
    replayOf: descriptor.replayOf,
    ...(descriptor.model !== null ? { model: descriptor.model } : {}),
    ...(descriptor.ttftMs !== null ? { ttftMs: descriptor.ttftMs } : {}),
  }
}

/** Field equality for an in-flight row, so recovery can keep an unchanged row's identity. */
function sameInFlight(a: InFlightRequest, b: InFlightRequest): boolean {
  return (
    a.seq === b.seq &&
    a.startedAt === b.startedAt &&
    a.sessionId === b.sessionId &&
    a.sessionName === b.sessionName &&
    a.method === b.method &&
    a.path === b.path &&
    a.backend === b.backend &&
    a.replayOf === b.replayOf &&
    a.model === b.model &&
    a.ttftMs === b.ttftMs &&
    a.tags.length === b.tags.length &&
    a.tags.every((tag, i) => tag === b.tags[i])
  )
}

/**
 * J0 — fold lifecycle frames, in id order, over an in-flight map. The one place the map's
 * shape is decided, so the coalescing flush and the post-recovery replay cannot drift apart.
 * Copy-on-write: `prev` is returned untouched when the window changed nothing, which is what
 * keeps a quiet tab from re-rendering on every flush.
 */
function applyLifecycle(
  prev: Map<number, InFlightRequest>,
  events: QueuedEvent[],
): Map<number, InFlightRequest> {
  let next: Map<number, InFlightRequest> | null = null
  const current = () => next ?? prev
  const edit = () => (next ??= new Map(prev))
  for (const event of events) {
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
