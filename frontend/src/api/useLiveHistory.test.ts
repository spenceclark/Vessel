import { createElement, type ReactNode } from 'react'
import { QueryClient, QueryClientProvider, useInfiniteQuery } from '@tanstack/react-query'
import { act, renderHook, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import {
  EMPTY_FILTERS,
  type ActiveRequestsResponse,
  type ClearState,
  type RequestListResponse,
  type Summary,
} from './types'
import { api } from './client'
import { requestsQueryKey } from './queryKeys'
import { useLiveHistory } from './useLiveHistory'

/**
 * R10/R11/R22/R23/D05 — the reconciliation model, exercised at the seams where its failures
 * actually happen: a completion racing an unsettled list fetch, lost SSE frames leaving
 * in-flight rows that never clear (now resolved by the server-authoritative active set,
 * F2), and a clear racing a buffered completion (F3). All were invisible to `tsc` and to the
 * backend suite, and reproduced by the review only under real timing.
 */

// ---- a controllable EventSource, since jsdom has none ---------------------------------

class FakeEventSource {
  static instances: FakeEventSource[] = []

  readonly url: string
  private readonly listeners = new Map<string, Set<(e: MessageEvent<string>) => void>>()
  closed = false

  constructor(url: string) {
    this.url = url
    FakeEventSource.instances.push(this)
  }

  addEventListener(type: string, fn: (e: MessageEvent<string>) => void) {
    if (!this.listeners.has(type)) this.listeners.set(type, new Set())
    this.listeners.get(type)!.add(fn)
  }

  close() {
    this.closed = true
  }

  /** Deliver one frame, with the SSE `id:` the server stamps on every publish. */
  emit(type: string, data: unknown, id?: number) {
    const event = new MessageEvent<string>(type, {
      data: JSON.stringify(data),
      lastEventId: id === undefined ? '' : String(id),
    })
    for (const fn of this.listeners.get(type) ?? []) fn(event)
  }

  open() {
    for (const fn of this.listeners.get('open') ?? []) fn(new MessageEvent('open') as MessageEvent<string>)
  }

  static latest() {
    return FakeEventSource.instances[FakeEventSource.instances.length - 1]
  }
}

// ---- fixtures --------------------------------------------------------------------------

const SESSION = 1

function summary(id: number, overrides: Partial<Summary> = {}): Summary {
  return {
    id,
    startedAt: `2026-08-28T00:00:0${id}.0000000Z`,
    sessionId: SESSION,
    backend: 'stub',
    tags: [],
    method: 'POST',
    path: `/api/chat?i=${id}`,
    format: 'ollama-chat',
    model: 'm',
    statusCode: 200,
    error: null,
    streamed: false,
    replayOf: null,
    durationMs: 10,
    ttftMs: null,
    vesselOverheadMs: 1,
    tokPerSec: null,
    tokensIn: 1,
    tokensOut: 1,
    tokensCachedRead: null,
    tokensCachedWrite: null,
    tokensEstimated: false,
    stopReason: 'stop',
    warnings: [],
    truncated: false,
    ...overrides,
  }
}

function startedEvent(seq: number, row: Summary) {
  return {
    seq,
    startedAt: row.startedAt,
    sessionId: row.sessionId,
    method: row.method,
    path: row.path,
    backend: row.backend,
    tags: row.tags,
  }
}

/** A list query mirroring RequestList's, so the hook operates on a real cache entry. */
function listPage(rows: Summary[]): RequestListResponse {
  return { rows, nextBefore: null }
}

/**
 * Stub the server's active-request set for the next reconciliation. `serverRunId` and `clear`
 * default to "one run, never cleared", so a test only states what it is about.
 */
function serverActive(
  active: Pick<ActiveRequestsResponse, 'activeSeqs' | 'newestCompletedSeq'> &
    Partial<ActiveRequestsResponse>,
) {
  vi.spyOn(api, 'getActiveRequests').mockResolvedValue({ serverRunId: 'run-1', clear: null, ...active })
}

/**
 * I0a — the versioned clear predicate the server publishes, identically on the `cleared` frame
 * and on `GET /active`. `boundaryId` is the largest row id that existed when a clear-all ran.
 */
function clearAll(version: number, boundaryId: number): ClearState {
  return { version, scope: 'all', beforeTs: null, boundaryId }
}

function clearBefore(version: number, beforeTs: string): ClearState {
  return { version, scope: 'before', beforeTs, boundaryId: 0 }
}

let originalEventSource: unknown

beforeEach(() => {
  FakeEventSource.instances = []
  originalEventSource = (globalThis as Record<string, unknown>).EventSource
  ;(globalThis as Record<string, unknown>).EventSource = FakeEventSource
})

afterEach(() => {
  ;(globalThis as Record<string, unknown>).EventSource = originalEventSource
  vi.restoreAllMocks()
})

function setup(options: { listFetch: () => Promise<RequestListResponse> }) {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: Infinity, staleTime: Infinity } },
  })

  const wrapper = ({ children }: { children: ReactNode }) =>
    createElement(QueryClientProvider, { client: queryClient }, children)

  // Mount the list query alongside the hook, exactly as the app does: the whole point of
  // R10 is the interaction between an unsettled fetch and an arriving completion.
  const rendered = renderHook(
    () => {
      const list = useInfiniteQuery({
        queryKey: requestsQueryKey(SESSION, EMPTY_FILTERS),
        queryFn: options.listFetch,
        initialPageParam: undefined as number | undefined,
        getNextPageParam: () => undefined,
      })
      const live = useLiveHistory({ scope: SESSION, filters: EMPTY_FILTERS })
      return { list, live }
    },
    { wrapper },
  )

  return { queryClient, rendered }
}

function cachedRowIds(queryClient: QueryClient): number[] {
  const data = queryClient.getQueryData<{ pages: RequestListResponse[] }>(
    requestsQueryKey(SESSION, EMPTY_FILTERS),
  )
  return (data?.pages ?? []).flatMap((p) => p.rows).map((r) => r.id)
}

describe('useLiveHistory', () => {
  // R10 — the exact case the review proved: the *initial* fetch has no cached data, so
  // TanStack reuses its promise instead of queueing a fresh one. A completion arriving
  // while it is unsettled used to be dropped and never reappear without a reload.
  it('keeps a completion that arrives during the initial list fetch', async () => {
    let resolveFetch: ((value: RequestListResponse) => void) | undefined
    const listFetch = () =>
      new Promise<RequestListResponse>((resolve) => {
        resolveFetch = resolve
      })

    const { queryClient, rendered } = setup({ listFetch })

    await waitFor(() => expect(resolveFetch).toBeDefined())
    await waitFor(() => expect(FakeEventSource.latest()).toBeDefined())

    const arriving = summary(42)
    act(() => {
      FakeEventSource.latest().emit('completed', { seq: 1, row: arriving }, 1)
    })

    // The fetch now resolves with a snapshot taken *before* that row existed.
    await act(async () => {
      resolveFetch!(listPage([]))
    })

    await waitFor(() => {
      expect(cachedRowIds(queryClient)).toContain(42)
    })

    rendered.unmount()
  })

  it('does not duplicate a completion the settling fetch already contained', async () => {
    let resolveFetch: ((value: RequestListResponse) => void) | undefined
    const listFetch = () =>
      new Promise<RequestListResponse>((resolve) => {
        resolveFetch = resolve
      })

    const { queryClient, rendered } = setup({ listFetch })
    await waitFor(() => expect(resolveFetch).toBeDefined())
    await waitFor(() => expect(FakeEventSource.latest()).toBeDefined())

    const arriving = summary(42)
    act(() => {
      FakeEventSource.latest().emit('completed', { seq: 1, row: arriving }, 1)
    })

    // This time the snapshot is newer and already includes the row.
    await act(async () => {
      resolveFetch!(listPage([arriving]))
    })

    await waitFor(() => {
      expect(cachedRowIds(queryClient)).toEqual([42])
    })

    rendered.unmount()
  })

  // D05 — in-flight entries are scoped by session and nothing else.
  it('exposes in-flight rows for the viewed session only', async () => {
    const { rendered } = setup({ listFetch: async () => listPage([]) })
    await waitFor(() => expect(FakeEventSource.latest()).toBeDefined())

    act(() => {
      FakeEventSource.latest().emit('started', startedEvent(1, summary(1)), 1)
      FakeEventSource.latest().emit(
        'started',
        startedEvent(2, summary(2, { sessionId: SESSION + 99 })),
        2,
      )
    })

    await waitFor(() => expect(rendered.result.current.live.inFlight).toHaveLength(1))
    expect(rendered.result.current.live.inFlight[0].seq).toBe(1)

    rendered.unmount()
  })

  it('clears an in-flight row on its completion', async () => {
    const { rendered } = setup({ listFetch: async () => listPage([]) })
    await waitFor(() => expect(FakeEventSource.latest()).toBeDefined())

    const row = summary(7)
    act(() => {
      FakeEventSource.latest().emit('started', startedEvent(1, row), 1)
    })
    await waitFor(() => expect(rendered.result.current.live.inFlight).toHaveLength(1))

    act(() => {
      FakeEventSource.latest().emit('completed', { seq: 1, row }, 2)
    })
    await waitFor(() => expect(rendered.result.current.live.inFlight).toHaveLength(0))

    rendered.unmount()
  })

  // R11/F2 — the review's stuck-rows case, now resolved server-authoritatively. A dropped
  // `completed` (bounded drop-oldest queues) leaves a row running unless the loss is
  // *detectable*: the gap in the publish id triggers reconciliation, and the server's active
  // set — not cached history — says the request is no longer running.
  it('clears an in-flight row whose completion was dropped, via the event-id gap and active set', async () => {
    const { rendered } = setup({ listFetch: async () => listPage([summary(7)]) })
    await waitFor(() => expect(FakeEventSource.latest()).toBeDefined())

    act(() => {
      FakeEventSource.latest().emit('started', startedEvent(1, summary(7)), 1)
      FakeEventSource.latest().emit('started', startedEvent(2, summary(8)), 2)
    })
    await waitFor(() => expect(rendered.result.current.live.inFlight).toHaveLength(2))

    // Seq 1 finished and was stored, but its `completed` frame was dropped: the next frame
    // jumps the publish id. The server reports only seq 2 still active, with seq 1 below the
    // completed boundary.
    serverActive({ activeSeqs: [2], newestCompletedSeq: 1 })
    act(() => {
      FakeEventSource.latest().emit('started', startedEvent(3, summary(9)), 9)
    })

    await waitFor(() => {
      expect(rendered.result.current.live.inFlight.map((i) => i.seq).sort()).toEqual([2, 3])
    })

    rendered.unmount()
  })

  // R11/F2 — the off-page repro the review reported: a completion lost, then 100 newer rows
  // stored, so the first history page no longer contains the request at all. Identity-in-
  // cached-pages reconciliation could never remove it; the server's active set does.
  it('removes an in-flight row that finished off the loaded history pages', async () => {
    // History returns only the newest rows — the finished request (seq 1) is not among them.
    const { rendered } = setup({ listFetch: async () => listPage([summary(9), summary(8)]) })
    await waitFor(() => expect(FakeEventSource.latest()).toBeDefined())

    act(() => {
      FakeEventSource.latest().open() // initial connect
      FakeEventSource.latest().emit('started', startedEvent(1, summary(1)), 1)
    })
    await waitFor(() => expect(rendered.result.current.live.inFlight).toHaveLength(1))

    serverActive({ activeSeqs: [], newestCompletedSeq: 100 })
    act(() => {
      FakeEventSource.latest().open() // reconnect forces reconciliation
    })

    await waitFor(() => expect(rendered.result.current.live.inFlight).toHaveLength(0))

    rendered.unmount()
  })

  // The other half of R11: a genuinely long-running request must survive reconciliation
  // because the server still lists it as active — never expired by a timer or seq distance.
  it('keeps a long-running in-flight row the server still reports as active', async () => {
    const { rendered } = setup({ listFetch: async () => listPage([summary(8), summary(9)]) })
    await waitFor(() => expect(FakeEventSource.latest()).toBeDefined())

    act(() => {
      FakeEventSource.latest().emit('started', startedEvent(1, summary(1)), 1)
    })
    await waitFor(() => expect(rendered.result.current.live.inFlight).toHaveLength(1))

    // Many other requests completed (boundary far ahead), but seq 1 is genuinely still
    // running, so the server keeps it in the active set.
    serverActive({ activeSeqs: [1], newestCompletedSeq: 500 })
    act(() => {
      FakeEventSource.latest().emit('started', startedEvent(2, summary(9)), 500)
    })

    await waitFor(() => expect(rendered.result.current.live.inFlight.map((i) => i.seq)).toContain(1))
    // Let the debounced reconciliation (150 ms) actually run; the long-running row must remain.
    await new Promise((r) => setTimeout(r, 300))
    expect(rendered.result.current.live.inFlight.map((i) => i.seq)).toContain(1)

    rendered.unmount()
  })

  // R11/F2 — a request that started *after* the server snapshot must not be expired: its
  // seq is above the completed boundary, so absence from the active set means "too new",
  // not "finished".
  it('does not expire a freshly-started row above the completed boundary', async () => {
    const { rendered } = setup({ listFetch: async () => listPage([]) })
    await waitFor(() => expect(FakeEventSource.latest()).toBeDefined())

    act(() => {
      FakeEventSource.latest().open() // initial connect
      FakeEventSource.latest().emit('started', startedEvent(10, summary(1)), 1)
    })
    await waitFor(() => expect(rendered.result.current.live.inFlight).toHaveLength(1))

    // Snapshot predates seq 10: it isn't in the active set, but it's above newestCompletedSeq.
    serverActive({ activeSeqs: [], newestCompletedSeq: 5 })
    act(() => {
      FakeEventSource.latest().open() // reconnect
    })

    // Past the 150 ms debounce: reconciliation ran, and must not have expired the fresh row.
    await new Promise((r) => setTimeout(r, 300))
    expect(rendered.result.current.live.inFlight.map((i) => i.seq)).toEqual([10])

    rendered.unmount()
  })

  // R11 — reconnect misses everything during the gap, so it reconciles unconditionally.
  it('reconciles after an EventSource reconnect', async () => {
    const { rendered } = setup({ listFetch: async () => listPage([summary(7)]) })

    await waitFor(() => expect(FakeEventSource.latest()).toBeDefined())
    act(() => {
      FakeEventSource.latest().open() // first connect
      FakeEventSource.latest().emit('started', startedEvent(1, summary(7)), 1)
    })
    await waitFor(() => expect(rendered.result.current.live.inFlight).toHaveLength(1))

    serverActive({ activeSeqs: [], newestCompletedSeq: 1 })
    act(() => {
      FakeEventSource.latest().open() // reconnect
    })

    await waitFor(() => expect(rendered.result.current.live.inFlight).toHaveLength(0))

    rendered.unmount()
  })

  // R22/F1 — a burst of gaps must coalesce into a single reconciliation, not one per gap
  // (the storm the review warned about). The debounce + single-flight guard collapse them.
  it('coalesces a burst of gaps into one reconciliation', async () => {
    const active = vi
      .spyOn(api, 'getActiveRequests')
      .mockResolvedValue({ activeSeqs: [1, 2, 3, 4], newestCompletedSeq: 0, serverRunId: 'run-1', clear: null })
    const { rendered } = setup({ listFetch: async () => listPage([]) })
    await waitFor(() => expect(FakeEventSource.latest()).toBeDefined())

    // Four started frames, each jumping the publish id — four gaps in quick succession.
    act(() => {
      FakeEventSource.latest().emit('started', startedEvent(1, summary(1)), 5)
      FakeEventSource.latest().emit('started', startedEvent(2, summary(2)), 20)
      FakeEventSource.latest().emit('started', startedEvent(3, summary(3)), 40)
      FakeEventSource.latest().emit('started', startedEvent(4, summary(4)), 60)
    })

    // Wait well past the debounce; despite four gaps, reconciliation runs exactly once.
    await new Promise((r) => setTimeout(r, 300))
    expect(active).toHaveBeenCalledTimes(1)

    rendered.unmount()
  })

  // R23/H0a — the review's repro: a completion buffered during a pending fetch must not
  // restore a row a clear-all deleted before the buffer drained. Cache must be [], not [1].
  // The clear is now an ordered SSE frame after the completion (on the wire a deleted row's
  // `completed` always precedes `cleared`), so the buffered row is purged, not the merge
  // suppressed by a generation counter.
  it('discards a buffered completion after a clear-all (initial fetch)', async () => {
    let resolveFetch: ((value: RequestListResponse) => void) | undefined
    const listFetch = () =>
      new Promise<RequestListResponse>((resolve) => {
        resolveFetch = resolve
      })

    const { queryClient, rendered } = setup({ listFetch })
    await waitFor(() => expect(resolveFetch).toBeDefined())
    await waitFor(() => expect(FakeEventSource.latest()).toBeDefined())

    // Completion for row 1 arrives while the initial fetch is still pending → buffered.
    // Then the clear-all frame arrives (next publish id) → purges the buffered row.
    act(() => {
      FakeEventSource.latest().emit('completed', { seq: 1, row: summary(1) }, 1)
      FakeEventSource.latest().emit('cleared', clearAll(1, 1), 2)
    })

    // The (post-clear) fetch resolves empty; draining must not resurrect row 1.
    await act(async () => {
      resolveFetch!(listPage([]))
    })

    // Past the I0c coalescing window, so the completion has really been applied (and purged),
    // rather than the cache merely being empty because nothing has landed yet.
    await new Promise((r) => setTimeout(r, 250))
    expect(cachedRowIds(queryClient)).toEqual([])

    rendered.unmount()
  })

  // R23/H0a — the review's third ordering: a newer fast request is persisted as id 1 (a
  // *later* startedAt); an older slow one as id 2 (an *earlier* startedAt). A clear-before by
  // timestamp deletes only the older row — even though its id is higher. The retired max-id
  // boundary would have dropped both (both ids ≤ 2); the startedAt predicate keeps id 1.
  it('keeps a clear-before survivor whose id is above the deleted one (inverted id/start order)', async () => {
    let resolveFetch: ((value: RequestListResponse) => void) | undefined
    const listFetch = () =>
      new Promise<RequestListResponse>((resolve) => {
        resolveFetch = resolve
      })

    const { queryClient, rendered } = setup({ listFetch })
    await waitFor(() => expect(resolveFetch).toBeDefined())
    await waitFor(() => expect(FakeEventSource.latest()).toBeDefined())

    const newerFast = summary(1, { startedAt: '2026-08-28T00:00:09.000Z' }) // survives
    const olderSlow = summary(2, { startedAt: '2026-08-28T00:00:02.000Z' }) // deleted

    act(() => {
      FakeEventSource.latest().emit('completed', { seq: 1, row: newerFast }, 1)
      FakeEventSource.latest().emit('completed', { seq: 2, row: olderSlow }, 2)
      FakeEventSource.latest().emit('cleared', clearBefore(1, '2026-08-28T00:00:05.000Z'), 3)
    })

    await act(async () => {
      resolveFetch!(listPage([]))
    })

    await waitFor(() => expect(cachedRowIds(queryClient)).toEqual([1]))
    expect(cachedRowIds(queryClient)).not.toContain(2)

    rendered.unmount()
  })

  // R23/H0a — a completion that arrives *after* the clear frame is post-clear by construction
  // (the wire order guarantees it), so it merges normally — including when SQLite reuses the
  // id of a row the clear just deleted.
  it('keeps a post-clear completion, even one reusing a cleared id', async () => {
    let resolveFetch: ((value: RequestListResponse) => void) | undefined
    const listFetch = () =>
      new Promise<RequestListResponse>((resolve) => {
        resolveFetch = resolve
      })

    const { queryClient, rendered } = setup({ listFetch })
    await waitFor(() => expect(resolveFetch).toBeDefined())
    await waitFor(() => expect(FakeEventSource.latest()).toBeDefined())

    act(() => {
      // A pre-clear row 1, then the clear-all, then a *new* row that reuses id 1 (SQLite id
      // reuse, R14b). The pre-clear row is purged; the post-clear one must survive.
      FakeEventSource.latest().emit('completed', { seq: 1, row: summary(1) }, 1)
      FakeEventSource.latest().emit('cleared', clearAll(1, 1), 2)
      FakeEventSource.latest().emit('completed', { seq: 2, row: summary(1, { path: '/api/chat?reused' }) }, 3)
    })

    await act(async () => {
      resolveFetch!(listPage([]))
    })

    await waitFor(() => expect(cachedRowIds(queryClient)).toEqual([1]))
    // It is the *post-clear* row, not the purged original.
    const rows = queryClient.getQueryData<{ pages: RequestListResponse[] }>(
      requestsQueryKey(SESSION, EMPTY_FILTERS),
    )
    expect(rows?.pages[0].rows[0].path).toBe('/api/chat?reused')

    rendered.unmount()
  })

  // R23/H0a — the review's first ordering: a clear commits and the list settles empty, and a
  // delayed completion for the deleted row then arrives. On the wire that completion still
  // precedes `cleared` (the row had to exist to be deleted), so the purge removes it and the
  // list stays empty rather than resurrecting the row.
  it('stays empty when a delayed completion for a cleared row arrives before the clear frame', async () => {
    const { queryClient, rendered } = setup({ listFetch: async () => listPage([]) })
    await waitFor(() => expect(FakeEventSource.latest()).toBeDefined())

    // The list is settled and empty; the completion merges (row reappears), then the clear
    // frame purges it back out.
    act(() => {
      FakeEventSource.latest().emit('completed', { seq: 1, row: summary(1) }, 1)
    })
    await waitFor(() => expect(cachedRowIds(queryClient)).toEqual([1]))

    act(() => {
      FakeEventSource.latest().emit('cleared', clearAll(1, 1), 2)
    })
    await waitFor(() => expect(cachedRowIds(queryClient)).toEqual([]))

    rendered.unmount()
  })

  // R11/H0b — restart repro (the review's Reproduction A). A `started(seq=100)` on one run;
  // then the connection reconnects onto a *restarted* Vessel whose active set is empty and
  // whose completed boundary is 0. A boundary comparison keeps seq 100 (100 > 0) forever; the
  // run-id change in the `hello` frame is what tells the client to discard the dead process's
  // seqs — with no further traffic.
  it('discards in-flight rows from a prior run after a restart (run-id change)', async () => {
    const { rendered } = setup({ listFetch: async () => listPage([]) })
    await waitFor(() => expect(FakeEventSource.latest()).toBeDefined())

    act(() => {
      FakeEventSource.latest().open()
      FakeEventSource.latest().emit('hello', { serverRunId: 'run-A' })
      FakeEventSource.latest().emit('started', startedEvent(100, summary(1)), 1)
    })
    await waitFor(() => expect(rendered.result.current.live.inFlight).toHaveLength(1))

    // Reconnect lands on a restarted process: fresh run id, empty active set, boundary 0.
    serverActive({ activeSeqs: [], newestCompletedSeq: 0, serverRunId: 'run-B' })
    act(() => {
      FakeEventSource.latest().open() // reconnect
      FakeEventSource.latest().emit('hello', { serverRunId: 'run-B' })
    })

    // No new traffic; the stale in-flight row must leave on the run-id change alone.
    await waitFor(() => expect(rendered.result.current.live.inFlight).toHaveLength(0))

    rendered.unmount()
  })

  // I0c — the crash mitigation, pinned as behaviour rather than a benchmark. A burst of
  // completions must reach the list cache in *one* write per coalescing window, not one per
  // frame: profiling a live 10k burst showed the per-frame version stalling the main thread
  // for 10.3 s in a single task while the heap climbed to 3.1 GB. Every row still arrives, in
  // order, so the guarantee this replaces (never lose a completion) is unchanged.
  it('applies a burst of completions in one cache write per window', async () => {
    const { queryClient, rendered } = setup({ listFetch: async () => listPage([]) })
    await waitFor(() => expect(FakeEventSource.latest()).toBeDefined())
    await waitFor(() => expect(cachedRowIds(queryClient)).toEqual([]))

    const key = requestsQueryKey(SESSION, EMPTY_FILTERS)
    let listWrites = 0
    const unsubscribe = queryClient.getQueryCache().subscribe((event) => {
      // `setQueryData` lands as a manual "success" update on that query.
      if (event.type === 'updated' && event.action.type === 'success'
        && JSON.stringify(event.query.queryKey) === JSON.stringify(key)) {
        listWrites++
      }
    })

    act(() => {
      for (let i = 1; i <= 40; i++) {
        FakeEventSource.latest().emit('started', startedEvent(i, summary(i)), i * 2 - 1)
        FakeEventSource.latest().emit('completed', { seq: i, row: summary(i) }, i * 2)
      }
    })

    await waitFor(() => expect(cachedRowIds(queryClient)).toHaveLength(40))
    unsubscribe()

    // Newest first, and every row present exactly once.
    expect(cachedRowIds(queryClient)[0]).toBe(40)
    expect(new Set(cachedRowIds(queryClient)).size).toBe(40)
    // One write for the window, not one per completion (the pre-I0c version made 40).
    expect(listWrites).toBeLessThanOrEqual(2)
    // ...and every in-flight row was retired by its completion.
    expect(rendered.result.current.live.inFlight).toHaveLength(0)

    rendered.unmount()
  })

  // R11/I0b(2) — the review's delivery order A. A recovery request issued against run A is
  // still pending when Vessel restarts: the hello for run B correctly discards A's entries,
  // and B's first request starts and is displayed. Then A's response resolves. Its run id
  // differs from the connection's, which used to be read as "the server restarted" and clear
  // the *whole current map* — erasing run B's live request, which no later snapshot restores
  // (reconciliation only removes). An obsolete response is evidence about nothing: it must be
  // discarded. Only the hello signals a run change.
  it('ignores a recovery response from an obsolete run instead of erasing the new run’s requests', async () => {
    const resolvers: ((value: ActiveRequestsResponse) => void)[] = []
    vi.spyOn(api, 'getActiveRequests').mockImplementation(
      () => new Promise<ActiveRequestsResponse>((resolve) => resolvers.push(resolve)),
    )

    const { rendered } = setup({ listFetch: async () => listPage([]) })
    await waitFor(() => expect(FakeEventSource.latest()).toBeDefined())

    act(() => {
      FakeEventSource.latest().open()
      FakeEventSource.latest().emit('hello', { serverRunId: 'run-A' })
      FakeEventSource.latest().emit('started', startedEvent(100, summary(1)), 1)
    })
    await waitFor(() => expect(rendered.result.current.live.inFlight).toHaveLength(1))

    // A gap on run A starts a recovery; its response is held, unresolved.
    act(() => {
      FakeEventSource.latest().emit('started', startedEvent(101, summary(2)), 9)
    })
    await waitFor(() => expect(resolvers).toHaveLength(1))

    // Vessel restarts underneath: the reconnected connection announces run B, and run B's
    // first request starts.
    act(() => {
      FakeEventSource.latest().open() // reconnect
      FakeEventSource.latest().emit('hello', { serverRunId: 'run-B' })
      FakeEventSource.latest().emit('started', startedEvent(1, summary(3)), 1)
    })
    await waitFor(() => expect(rendered.result.current.live.inFlight.map((i) => i.seq)).toEqual([1]))

    // Run A's snapshot finally resolves: empty, with a far-ahead boundary in *A's* seq space.
    await act(async () => {
      resolvers[0]({ activeSeqs: [], newestCompletedSeq: 500, serverRunId: 'run-A', clear: null })
      await new Promise((r) => setTimeout(r, 20))
    })

    // Run B's request is still live: [1], not [].
    expect(rendered.result.current.live.inFlight.map((i) => i.seq)).toEqual([1])

    rendered.unmount()
  })

  // R23/I0a — the review's delivery order A. A pre-clear database snapshot is held in an
  // unsettled *initial* list fetch, so there is nothing in the cache for the `cleared` frame to
  // purge. The DELETE ack's invalidation cannot save it either: TanStack reuses an in-flight
  // initial request rather than replacing it. When it finally resolves, its stale row lands in
  // the cache. Re-applying the clear predicate once list fetching settles is what removes it.
  it('purges a pre-clear list snapshot that settles after the clear', async () => {
    let resolveFetch: ((value: RequestListResponse) => void) | undefined
    let fetches = 0
    const listFetch = () => {
      fetches++
      // The first (held) fetch carries the pre-clear snapshot; anything issued afterwards
      // reads a post-clear database and is legitimately empty.
      return fetches === 1
        ? new Promise<RequestListResponse>((resolve) => {
            resolveFetch = resolve
          })
        : Promise.resolve(listPage([]))
    }

    const { queryClient, rendered } = setup({ listFetch })
    await waitFor(() => expect(resolveFetch).toBeDefined())
    await waitFor(() => expect(FakeEventSource.latest()).toBeDefined())

    // The clear commits and its frame arrives while that fetch is still unsettled.
    act(() => {
      FakeEventSource.latest().emit('cleared', clearAll(1, 1), 1)
    })

    // DataPanel's ack path invalidates the list while the initial request is pending (not
    // awaited: it resolves only when that request does).
    void queryClient.invalidateQueries({ queryKey: ['requests'] })

    // The held response resolves with the row the clear deleted.
    await act(async () => {
      resolveFetch!(listPage([summary(1)]))
    })

    await waitFor(() => expect(cachedRowIds(queryClient)).toEqual([]))

    rendered.unmount()
  })

  // R23/I0a — the review's delivery order B. The `cleared` frame is dropped by the bounded
  // drop-oldest queue, so the client never sees it; the loss shows up only as an id gap.
  // Recovery must therefore carry the deletion state: `GET /active` reports the same versioned
  // predicate, which purges the completion buffer before it drains into the authoritative
  // empty page. Correctness cannot depend on that frame surviving a deliberately lossy feed.
  it('applies a clear it never saw, from the version on /active, to the completion buffer', async () => {
    let resolveFetch: ((value: RequestListResponse) => void) | undefined
    let fetches = 0
    const listFetch = () => {
      fetches++
      return fetches === 1
        ? new Promise<RequestListResponse>((resolve) => {
            resolveFetch = resolve
          })
        : Promise.resolve(listPage([]))
    }

    const { queryClient, rendered } = setup({ listFetch })
    await waitFor(() => expect(resolveFetch).toBeDefined())
    await waitFor(() => expect(FakeEventSource.latest()).toBeDefined())

    // Row 1 completes while the list fetch is unsettled → buffered. The server then clears it,
    // but the `cleared` frame never arrives.
    act(() => {
      FakeEventSource.latest().emit('completed', { seq: 1, row: summary(1) }, 1)
    })

    serverActive({ activeSeqs: [], newestCompletedSeq: 1, clear: clearAll(1, 1) })

    // A later frame reveals the gap and triggers recovery.
    act(() => {
      FakeEventSource.latest().emit('started', startedEvent(2, summary(2)), 5)
    })

    // Past the 150 ms debounce, so /active has been read and its clear applied to the buffer.
    await act(async () => {
      await new Promise((r) => setTimeout(r, 250))
    })

    // History resolves authoritatively empty; the buffer must not remerge the deleted row.
    await act(async () => {
      resolveFetch!(listPage([]))
      await new Promise((r) => setTimeout(r, 20))
    })

    expect(cachedRowIds(queryClient)).toEqual([])

    rendered.unmount()
  })

  // R23/I0a — the other half of re-application: it must be idempotent for live rows. A
  // post-clear completion whose row reuses a cleared id (SQLite restarts ids once a clear-all
  // empties the table, so it sits *below* the boundary) survives the purge at settlement, and
  // survives a later refetch settling too — the predicate is retired once every fetch that
  // could predate the clear has landed.
  it('keeps a post-clear row reusing a cleared id across refetch settlement', async () => {
    let resolveFetch: ((value: RequestListResponse) => void) | undefined
    let fetches = 0
    const reused = summary(1, { path: '/api/chat?reused' })
    const listFetch = () => {
      fetches++
      return fetches === 1
        ? new Promise<RequestListResponse>((resolve) => {
            resolveFetch = resolve
          })
        : Promise.resolve(listPage([reused]))
    }

    const { queryClient, rendered } = setup({ listFetch })
    await waitFor(() => expect(resolveFetch).toBeDefined())
    await waitFor(() => expect(FakeEventSource.latest()).toBeDefined())

    act(() => {
      FakeEventSource.latest().emit('completed', { seq: 1, row: summary(1) }, 1)
      FakeEventSource.latest().emit('cleared', clearAll(1, 1), 2)
      FakeEventSource.latest().emit('completed', { seq: 2, row: reused }, 3)
    })

    await act(async () => {
      resolveFetch!(listPage([]))
    })
    await waitFor(() => expect(cachedRowIds(queryClient)).toEqual([1]))

    // A second, post-clear fetch settles carrying the same row. Nothing may purge it.
    await act(async () => {
      await queryClient.refetchQueries({ queryKey: ['requests'] })
      await new Promise((r) => setTimeout(r, 20))
    })

    expect(cachedRowIds(queryClient)).toEqual([1])

    rendered.unmount()
  })
})
