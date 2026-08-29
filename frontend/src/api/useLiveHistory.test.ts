import { createElement, type ReactNode } from 'react'
import { QueryClient, QueryClientProvider, useInfiniteQuery } from '@tanstack/react-query'
import { act, renderHook, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import {
  EMPTY_FILTERS,
  type ActiveDescriptor,
  type ActiveRequestsResponse,
  type RequestListResponse,
  type Summary,
} from './types'
import { api } from './client'
import { requestsQueryKey } from './queryKeys'
import { useLiveHistory } from './useLiveHistory'

/**
 * R10/R11/R22/R23/D05, under the **J0** recovery contract — the reconciliation model exercised
 * at the seams where its failures actually happen: a completion racing an unsettled list fetch,
 * lost SSE frames leaving in-flight rows that never clear, and a clear racing queued work,
 * recovery, and REST snapshots taken either side of it. All were invisible to `tsc` and to the
 * backend suite, and every one was reproduced by a review only under real timing.
 *
 * J0's two operations are what these tests pin: **recovery is wholesale replacement** against a
 * server snapshot taken at a log position, and **between recoveries frames apply in id order**.
 * The fixtures must therefore be *physically consistent* — see `serverActive`.
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

/**
 * K0b/R27 — the same request as the recovery snapshot describes it. The server builds this
 * from what it received at registration, so it carries exactly what the `started` frame
 * carried (plus the model, once parsed, and the live TTFT, once measured) — which is what lets
 * recovery render a request whose frame the client never saw, or restore a live metric whose
 * frame was dropped.
 */
function activeDescriptor(
  seq: number,
  row: Summary,
  model: string | null = null,
  ttftMs: number | null = null,
): ActiveDescriptor {
  return { ...startedEvent(seq, row), sessionId: row.sessionId ?? SESSION, model, ttftMs }
}

/** A list query mirroring RequestList's, so the hook operates on a real cache entry. */
function listPage(rows: Summary[]): RequestListResponse {
  return { rows, nextBefore: null }
}

/**
 * Stub the recovery snapshot for the next reconciliation. `serverRunId` defaults to the one run
 * these tests use, so a test only states what it is about.
 *
 * **`logPosition` is not free.** It is the publish id the active set is true as of, and the
 * server takes it *after* the client's request goes out — so every frame the client received
 * before that request necessarily has an id at or below it. A fixture pairing a low
 * `logPosition` with frames the client already saw describes a server that cannot exist, and
 * would be testing the client against physics rather than against the contract.
 */
function serverActive(active: { active: ActiveDescriptor[]; logPosition: number; serverRunId?: string }) {
  vi.spyOn(api, 'getActiveRequests').mockResolvedValue({ serverRunId: 'run-1', ...active })
}

/** Past the 100 ms coalescing window and the 150 ms recovery debounce, plus slack. */
async function settle(ms = 300) {
  await act(async () => {
    await new Promise((r) => setTimeout(r, ms))
  })
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

function inFlightSeqs(rendered: { result: { current: { live: { inFlight: { seq: number }[] } } } }): number[] {
  return rendered.result.current.live.inFlight.map((i) => i.seq).sort((a, b) => a - b)
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

  // R11/F2 — the review's stuck-rows case. A dropped `completed` (bounded drop-oldest queues)
  // leaves a row running unless the loss is *detectable*: the gap in the publish id triggers
  // recovery, and the server's active set — not cached history — is adopted wholesale.
  it('clears an in-flight row whose completion was dropped, via the event-id gap and active set', async () => {
    const { rendered } = setup({ listFetch: async () => listPage([summary(7)]) })
    await waitFor(() => expect(FakeEventSource.latest()).toBeDefined())

    act(() => {
      FakeEventSource.latest().emit('started', startedEvent(1, summary(7)), 1)
      FakeEventSource.latest().emit('started', startedEvent(2, summary(8)), 2)
    })
    await waitFor(() => expect(rendered.result.current.live.inFlight).toHaveLength(2))

    // Seq 1 finished and was stored, but its `completed` frame was dropped: the next frame
    // jumps the publish id. Seq 3's `started` was published (id 9) before the client asked for
    // this snapshot, so the snapshot necessarily covers it and lists it as active — the
    // fixture says so because the server could not answer any other way.
    serverActive({
      active: [activeDescriptor(2, summary(8)), activeDescriptor(3, summary(9))],
      logPosition: 9,
    })
    act(() => {
      FakeEventSource.latest().emit('started', startedEvent(3, summary(9)), 9)
    })

    await waitFor(() => expect(inFlightSeqs(rendered)).toEqual([2, 3]))

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

    serverActive({ active: [], logPosition: 100 })
    act(() => {
      FakeEventSource.latest().open() // reconnect forces recovery
    })

    await waitFor(() => expect(rendered.result.current.live.inFlight).toHaveLength(0))

    rendered.unmount()
  })

  // The other half of R11: a genuinely long-running request must survive recovery because the
  // server still lists it as active — never expired by a timer or by seq distance.
  it('keeps a long-running in-flight row the server still reports as active', async () => {
    const { rendered } = setup({ listFetch: async () => listPage([summary(8), summary(9)]) })
    await waitFor(() => expect(FakeEventSource.latest()).toBeDefined())

    act(() => {
      FakeEventSource.latest().emit('started', startedEvent(1, summary(1)), 1)
    })
    await waitFor(() => expect(rendered.result.current.live.inFlight).toHaveLength(1))

    // Hundreds of frames later, seq 1 is genuinely still running — so it is in the snapshot,
    // and wholesale replacement keeps it.
    serverActive({
      active: [activeDescriptor(1, summary(1)), activeDescriptor(2, summary(9))],
      logPosition: 500,
    })
    act(() => {
      FakeEventSource.latest().emit('started', startedEvent(2, summary(9)), 500)
    })

    await settle()
    expect(inFlightSeqs(rendered)).toEqual([1, 2])

    rendered.unmount()
  })

  // J0 rule 2, second half — "queued events with id > logPosition replay in order on top".
  // A request that starts *while the recovery is in flight* is above the snapshot's position,
  // so the snapshot is not evidence about it: it is held (the flush is suspended for exactly
  // this reason) and replayed after the recovered state lands. This replaces the retired
  // "above the completed boundary" rule, which asked the client to reason about seq numbers.
  it('replays a start that arrives while recovery is in flight, above the snapshot position', async () => {
    const resolvers: ((value: ActiveRequestsResponse) => void)[] = []
    vi.spyOn(api, 'getActiveRequests').mockImplementation(
      () => new Promise<ActiveRequestsResponse>((resolve) => resolvers.push(resolve)),
    )

    const { rendered } = setup({ listFetch: async () => listPage([]) })
    await waitFor(() => expect(FakeEventSource.latest()).toBeDefined())

    act(() => {
      FakeEventSource.latest().open() // initial connect
      FakeEventSource.latest().open() // reconnect → recovery
    })
    await waitFor(() => expect(resolvers).toHaveLength(1))

    // The snapshot request is out; this start is published after it and cannot be in it.
    act(() => {
      FakeEventSource.latest().emit('started', startedEvent(10, summary(1)), 9)
    })

    await act(async () => {
      resolvers[0]({ active: [], logPosition: 5, serverRunId: 'run-1' })
      await new Promise((r) => setTimeout(r, 250))
    })

    expect(inFlightSeqs(rendered)).toEqual([10])

    rendered.unmount()
  })

  // R11 — reconnect misses everything during the gap, so it recovers unconditionally.
  it('reconciles after an EventSource reconnect', async () => {
    const { rendered } = setup({ listFetch: async () => listPage([summary(7)]) })

    await waitFor(() => expect(FakeEventSource.latest()).toBeDefined())
    act(() => {
      FakeEventSource.latest().open() // first connect
      FakeEventSource.latest().emit('started', startedEvent(1, summary(7)), 1)
    })
    await waitFor(() => expect(rendered.result.current.live.inFlight).toHaveLength(1))

    serverActive({ active: [], logPosition: 2 })
    act(() => {
      FakeEventSource.latest().open() // reconnect
    })

    await waitFor(() => expect(rendered.result.current.live.inFlight).toHaveLength(0))

    rendered.unmount()
  })

  // R22/F1 — a burst of gaps must coalesce into a single recovery, not one per gap (the storm
  // the review warned about). The debounce + single-flight guard collapse them.
  it('coalesces a burst of gaps into one reconciliation', async () => {
    const active = vi
      .spyOn(api, 'getActiveRequests')
      .mockResolvedValue({
        active: [1, 2, 3, 4].map((seq) => activeDescriptor(seq, summary(seq))),
        logPosition: 60,
        serverRunId: 'run-1',
      })
    const { rendered } = setup({ listFetch: async () => listPage([]) })
    await waitFor(() => expect(FakeEventSource.latest()).toBeDefined())

    // Four started frames, each jumping the publish id — four gaps in quick succession.
    act(() => {
      FakeEventSource.latest().emit('started', startedEvent(1, summary(1)), 5)
      FakeEventSource.latest().emit('started', startedEvent(2, summary(2)), 20)
      FakeEventSource.latest().emit('started', startedEvent(3, summary(3)), 40)
      FakeEventSource.latest().emit('started', startedEvent(4, summary(4)), 60)
    })

    // Wait well past the debounce; despite four gaps, recovery runs exactly once.
    await settle()
    expect(active).toHaveBeenCalledTimes(1)

    rendered.unmount()
  })

  // R23/J0 rule 3 — a completion buffered during a pending fetch must not restore a row a
  // clear-all deleted before the buffer drained. The clear is an ordered frame, and at its
  // position everything the client is holding goes: cache, buffer, and rows completed earlier
  // in the same window. Nothing is inspected — no predicate, no boundary.
  it('discards a buffered completion after a clear-all (initial fetch)', async () => {
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

    // Completion for row 1 arrives while the initial fetch is still pending → buffered.
    // Then the clear-all frame arrives (next publish id) → drops the buffered row.
    act(() => {
      FakeEventSource.latest().emit('completed', { seq: 1, row: summary(1) }, 1)
      FakeEventSource.latest().emit('cleared', {}, 2)
    })

    // The (pre-clear) held response resolves; nothing may resurrect row 1.
    await act(async () => {
      resolveFetch!(listPage([]))
    })

    await settle()
    expect(cachedRowIds(queryClient)).toEqual([])

    rendered.unmount()
  })

  // R23/J0 rule 4 — a clear-before deletes some rows and leaves others. The client does not
  // decide which: it drops what it holds and lets the refetch, which reads the post-clear
  // database, say what survived. The retired predicate model got this "right" by re-deriving
  // the server's WHERE clause client-side; this gets it right by not deciding at all.
  it('restores clear-before survivors from the refetch instead of filtering them client-side', async () => {
    const survivor = summary(1, { startedAt: '2026-08-28T00:00:09.000Z' })
    const deleted = summary(2, { startedAt: '2026-08-28T00:00:02.000Z' })

    let fetches = 0
    const listFetch = () => {
      fetches++
      // The first fetch reads a pre-clear database; every later one is post-clear.
      return Promise.resolve(listPage(fetches === 1 ? [survivor, deleted] : [survivor]))
    }

    const { queryClient, rendered } = setup({ listFetch })
    await waitFor(() => expect(cachedRowIds(queryClient)).toEqual([1, 2]))
    await waitFor(() => expect(FakeEventSource.latest()).toBeDefined())

    act(() => {
      FakeEventSource.latest().emit('cleared', {}, 1)
    })

    await waitFor(() => expect(cachedRowIds(queryClient)).toEqual([1]))
    await settle()
    // Still exactly the survivor: no client-side rule ever removes a row a fetch returned.
    expect(cachedRowIds(queryClient)).toEqual([1])

    rendered.unmount()
  })

  // R23 — a completion that arrives *after* the clear frame is post-clear by wire order, so it
  // merges normally, including when SQLite reuses the id of a row the clear just deleted.
  it('keeps a post-clear completion, even one reusing a cleared id', async () => {
    const reused = summary(1, { path: '/api/chat?reused' })
    let fetches = 0
    const listFetch = () => {
      fetches++
      return Promise.resolve(listPage(fetches === 1 ? [summary(1)] : []))
    }

    const { queryClient, rendered } = setup({ listFetch })
    await waitFor(() => expect(cachedRowIds(queryClient)).toEqual([1]))
    await waitFor(() => expect(FakeEventSource.latest()).toBeDefined())

    act(() => {
      // The clear-all, then a *new* row that reuses id 1 (SQLite id reuse, R14b).
      FakeEventSource.latest().emit('cleared', {}, 1)
      FakeEventSource.latest().emit('completed', { seq: 2, row: reused }, 2)
    })

    await waitFor(() => expect(cachedRowIds(queryClient)).toEqual([1]))
    await settle()

    // It is the *post-clear* row, not the purged original.
    const rows = queryClient.getQueryData<{ pages: RequestListResponse[] }>(
      requestsQueryKey(SESSION, EMPTY_FILTERS),
    )
    expect(rows?.pages.flatMap((p) => p.rows).map((r) => r.path)).toEqual(['/api/chat?reused'])

    rendered.unmount()
  })

  // R23 — the review's first ordering: a clear commits and the list settles empty, and a
  // delayed completion for the deleted row arrives first. On the wire that completion still
  // precedes `cleared` (the row had to exist to be deleted), so applying frames in id order
  // merges it and then drops it again, leaving the list empty rather than resurrecting it.
  it('stays empty when a delayed completion for a cleared row arrives before the clear frame', async () => {
    const { queryClient, rendered } = setup({ listFetch: async () => listPage([]) })
    await waitFor(() => expect(FakeEventSource.latest()).toBeDefined())

    act(() => {
      FakeEventSource.latest().emit('completed', { seq: 1, row: summary(1) }, 1)
    })
    await waitFor(() => expect(cachedRowIds(queryClient)).toEqual([1]))

    act(() => {
      FakeEventSource.latest().emit('cleared', {}, 2)
    })
    await waitFor(() => expect(cachedRowIds(queryClient)).toEqual([]))
    await settle()
    expect(cachedRowIds(queryClient)).toEqual([])

    rendered.unmount()
  })

  // R11/H0b — restart repro (the review's Reproduction A). A `started(seq=100)` on one run;
  // then the connection reconnects onto a *restarted* Vessel whose active set is empty. Seqs
  // and log positions both restart with the process, so nothing from the old run can be
  // compared against the new one: the run-id change in the `hello` frame discards all of it.
  it('discards in-flight rows from a prior run after a restart (run-id change)', async () => {
    const { rendered } = setup({ listFetch: async () => listPage([]) })
    await waitFor(() => expect(FakeEventSource.latest()).toBeDefined())

    act(() => {
      FakeEventSource.latest().open()
      FakeEventSource.latest().emit('hello', { serverRunId: 'run-A' })
      FakeEventSource.latest().emit('started', startedEvent(100, summary(1)), 1)
    })
    await waitFor(() => expect(rendered.result.current.live.inFlight).toHaveLength(1))

    // Reconnect lands on a restarted process: fresh run id, empty active set, position 0.
    serverActive({ active: [], logPosition: 0, serverRunId: 'run-B' })
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
  // the *whole current map* — erasing run B's live request, which no later snapshot restores.
  // An obsolete response is evidence about nothing: it must be discarded.
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
    await waitFor(() => expect(inFlightSeqs(rendered)).toEqual([1]))

    // Run A's snapshot finally resolves: empty, at a far-ahead position in *A's* log.
    await act(async () => {
      resolvers[0]({ active: [], logPosition: 500, serverRunId: 'run-A' })
      await new Promise((r) => setTimeout(r, 250))
    })

    // Run B's request is still live: [1], not [].
    expect(inFlightSeqs(rendered)).toEqual([1])

    rendered.unmount()
  })

  // ---- the round-five review's four remaining cases (§2.1, §2.2 A/B/C) -------------------

  // §2.1 (R11) — a queued start must not undo an authoritative recovery. The start waits in
  // the coalescing window; the server finishes the request and its `completed` is lost; the
  // pending snapshot comes back with an empty active set at a position *above* that start.
  // Under I0c+I0b the batch landed after the recovery and reinserted seq 1 as running for
  // ever (review: actual [1], expected []). Under J0 the arithmetic decides it: the start is
  // at or below the snapshot position, so the snapshot already accounts for it.
  it('does not let a queued start undo a recovery that already accounts for it', async () => {
    const resolvers: ((value: ActiveRequestsResponse) => void)[] = []
    vi.spyOn(api, 'getActiveRequests').mockImplementation(
      () => new Promise<ActiveRequestsResponse>((resolve) => resolvers.push(resolve)),
    )

    const { rendered } = setup({ listFetch: async () => listPage([summary(1)]) })
    await waitFor(() => expect(FakeEventSource.latest()).toBeDefined())

    act(() => {
      FakeEventSource.latest().open()
      FakeEventSource.latest().open() // reconnect → recovery pending
    })
    await waitFor(() => expect(resolvers).toHaveLength(1))

    // started(seq=1) reaches the client and waits in the 100 ms window.
    act(() => {
      FakeEventSource.latest().emit('started', startedEvent(1, summary(1)), 3)
    })

    // The server finished seq 1 (its `completed`, id 4, was lost) before taking this snapshot.
    await act(async () => {
      resolvers[0]({ active: [], logPosition: 5, serverRunId: 'run-1' })
      await new Promise((r) => setTimeout(r, 250))
    })

    expect(inFlightSeqs(rendered)).toEqual([])

    rendered.unmount()
  })

  // §2.2 A — recovery overtakes a queued pre-clear completion. The completion is queued, the
  // clear that deleted its row is *lost*, and the pending snapshot returns from after both.
  // Under I0a the queued completion was classified post-clear merely because it was *applied*
  // later, and was merged back after the authoritative empty refetch (review: actual [1],
  // expected []). Under J0 it is below the snapshot position, so it is discarded with the
  // rest of the pre-snapshot work; the refetch is the only thing that decides the list.
  it('discards a queued pre-clear completion that a recovery already accounts for', async () => {
    const resolvers: ((value: ActiveRequestsResponse) => void)[] = []
    vi.spyOn(api, 'getActiveRequests').mockImplementation(
      () => new Promise<ActiveRequestsResponse>((resolve) => resolvers.push(resolve)),
    )

    let fetches = 0
    const listFetch = () => {
      fetches++
      // The first read predates the clear; everything later reads the cleared database.
      return Promise.resolve(listPage(fetches === 1 ? [] : []))
    }

    const { queryClient, rendered } = setup({ listFetch })
    await waitFor(() => expect(FakeEventSource.latest()).toBeDefined())

    // Establish the id watermark, then a gap → recovery, whose response is held.
    act(() => {
      FakeEventSource.latest().emit('started', startedEvent(2, summary(2)), 1)
      FakeEventSource.latest().emit('started', startedEvent(3, summary(3)), 3)
    })
    await waitFor(() => expect(resolvers).toHaveLength(1))

    // The completion arrives while the recovery is in flight but was published before the
    // snapshot; the `cleared` frame that deleted its row (id 5) never arrives at all.
    act(() => {
      FakeEventSource.latest().emit('completed', { seq: 1, row: summary(1) }, 4)
    })

    await act(async () => {
      resolvers[0]({
        active: [activeDescriptor(3, summary(3))],
        logPosition: 6,
        serverRunId: 'run-1',
      })
      await new Promise((r) => setTimeout(r, 250))
    })

    expect(cachedRowIds(queryClient)).toEqual([])
    expect(inFlightSeqs(rendered)).toEqual([3])

    rendered.unmount()
  })

  // §2.2 B — a valid post-clear REST row whose `completed` frame was lost. A list request is
  // outstanding when clear-all deletes row 1; a new row then reuses id 1, and the outstanding
  // request's database snapshot is taken after that insert, so it returns the new row.
  // Under I0a the armed re-application purged it (`id <= boundaryId`) because no SSE
  // completion was there to exempt it (review: actual [], expected [1]). Under J0 nothing the
  // client holds can delete a row a fetch returned — rule 4, with no exceptions to arrange.
  it('keeps a valid post-clear row that reuses a cleared id and has no completion frame', async () => {
    const reused = summary(1, { path: '/api/chat?reused' })
    let resolveFetch: ((value: RequestListResponse) => void) | undefined
    let fetches = 0
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

    // The clear commits while that fetch is outstanding.
    act(() => {
      FakeEventSource.latest().emit('cleared', {}, 1)
    })

    // The outstanding request took its snapshot after the new row was inserted, so it returns
    // the reused id — and its `completed` frame never arrives.
    await act(async () => {
      resolveFetch!(listPage([reused]))
      await new Promise((r) => setTimeout(r, 250))
    })

    expect(cachedRowIds(queryClient)).toEqual([1])
    const rows = queryClient.getQueryData<{ pages: RequestListResponse[] }>(
      requestsQueryKey(SESSION, EMPTY_FILTERS),
    )
    expect(rows?.pages.flatMap((p) => p.rows).map((r) => r.path)).toEqual(['/api/chat?reused'])

    rendered.unmount()
  })

  // §2.2 C — two clears, the first one missed. Clear-all v1 deletes the buffered row but its
  // frame is lost; a later clear-before v2 (an earlier cutoff, deleting nothing) is the one
  // the client sees. Under I0a the server retained only the latest clear, and neither v2's
  // predicate nor its version described v1's deletion, so the buffer restored the row
  // (review: actual [1], expected []). Under J0 there is no predicate to be wrong: the clear
  // is a position, everything held at it is dropped, and the refetch reads a database that
  // reflects *both* clears.
  it('forgets a row deleted by a clear it never saw, when a later clear arrives', async () => {
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

    // Row 1 completes while the list fetch is unsettled → buffered.
    act(() => {
      FakeEventSource.latest().emit('completed', { seq: 1, row: summary(1) }, 1)
    })

    // Clear-all v1 (id 2) deletes it, and that frame is lost. Clear-before v2 (id 3) arrives.
    serverActive({ active: [], logPosition: 3 })
    act(() => {
      FakeEventSource.latest().emit('cleared', {}, 3)
    })

    await act(async () => {
      resolveFetch!(listPage([]))
      await new Promise((r) => setTimeout(r, 300))
    })

    expect(cachedRowIds(queryClient)).toEqual([])

    rendered.unmount()
  })

  // ---- earlier controlled-delivery cases, re-expressed against J0 ------------------------

  // R23/K0a — the review's §2.1 sequence 1, with the timing that actually exposes the bug.
  // A pre-clear database snapshot is held in an unsettled *initial* list fetch, so there is
  // nothing in the cache for the `cleared` frame to purge, and TanStack reuses an in-flight
  // initial request rather than starting a second one.
  //
  // **The timing is the test.** The previous version released the held response immediately
  // after emitting the frame — before the 100 ms clear batch ran — so the stale response had
  // already settled by the time the clear handler fired, and the refetch that followed made it
  // pass for the wrong reason. Releasing it only *after* the batch (and after asserting the
  // second fetch has started) is what reproduces round six's failure: without cancellation the
  // trigger waits on the very request it is meant to supersede, and row 1 comes back.
  it('converges on the post-clear list when a pre-clear snapshot settles after the clear', async () => {
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
    expect(fetches).toBe(1)

    act(() => {
      FakeEventSource.latest().emit('cleared', {}, 1)
    })

    // DataPanel's ack path also invalidates the list while the initial request is pending.
    void queryClient.invalidateQueries({ queryKey: ['requests'] })

    // Past the clear's coalescing window: its refetch must have *started* a second request,
    // rather than adopting the pending one, while the first is still unresolved.
    await settle()
    expect(fetches).toBeGreaterThanOrEqual(2)

    // Only now does the pre-clear snapshot come back, carrying the row the clear deleted. It
    // was cancelled, so it can no longer become the authoritative answer.
    await act(async () => {
      resolveFetch!(listPage([summary(1)]))
      await new Promise((r) => setTimeout(r, 300))
    })

    expect(cachedRowIds(queryClient)).toEqual([])

    rendered.unmount()
  })

  // R23/K0a — the review's §2.1 sequence 2: the same pending initial fetch, but the clear is
  // learned through *recovery* rather than from a frame. The completion and the `cleared` frame
  // are both lost; a later frame exposes the id gap; `/active` answers with a post-clear
  // snapshot. Recovery's authoritative read has the same obligation as the clear's, and the
  // same TanStack behaviour defeated it: the refetch adopted the pending pre-clear request,
  // whose response then restored the deleted row.
  it('supersedes a pending pre-clear fetch when the clear is learned through recovery', async () => {
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
    expect(fetches).toBe(1)

    // Frame 1 sets the watermark; the completion (2) and the clear (3) are dropped; frame 4
    // exposes the gap and triggers recovery against a post-clear database.
    serverActive({ active: [], logPosition: 4 })
    act(() => {
      FakeEventSource.latest().emit('started', startedEvent(9, summary(9)), 1)
      FakeEventSource.latest().emit('first_token', { seq: 9, ttftMs: 12 }, 4)
    })

    await settle()
    expect(fetches).toBeGreaterThanOrEqual(2)

    await act(async () => {
      resolveFetch!(listPage([summary(1)]))
      await new Promise((r) => setTimeout(r, 300))
    })

    expect(cachedRowIds(queryClient)).toEqual([])

    rendered.unmount()
  })

  // R11/K0b — the review's §2.2 sequence. A `started` frame is dropped by the bounded queue,
  // so the client has no record of the request at all; the gap is exposed by a later frame for
  // that same request while it is still running. Recovery must *show* it, not merely know it:
  // the snapshot describes each active request, so the row is rebuilt from the server's own
  // registration data. Before K0b the client intersected the active set with its known starts
  // and rendered nothing — a long-running request could stay invisible for its whole duration.
  it('displays an active request whose started frame was lost, from the recovery snapshot', async () => {
    const { rendered } = setup({ listFetch: async () => listPage([]) })
    await waitFor(() => expect(FakeEventSource.latest()).toBeDefined())

    const lost = summary(2, { path: '/api/chat?lost-start', tags: ['t-lost'] })
    serverActive({ active: [activeDescriptor(2, lost, 'qwen2.5:1.5b')], logPosition: 3 })

    act(() => {
      FakeEventSource.latest().open()
      FakeEventSource.latest().emit('hello', { serverRunId: 'run-1' })
      // Frame 1 establishes the watermark; frame 2 (started, seq 2) is dropped; frame 3 is
      // `request_ready` for that same still-running request, which exposes the gap.
      FakeEventSource.latest().emit('started', startedEvent(1, summary(1)), 1)
      FakeEventSource.latest().emit('request_ready', { seq: 2, model: 'qwen2.5:1.5b' }, 3)
    })

    await settle()

    const row = rendered.result.current.live.inFlight.find((r) => r.seq === 2)
    expect(row).toBeDefined()
    expect(row?.path).toBe('/api/chat?lost-start')
    expect(row?.method).toBe('POST')
    expect(row?.backend).toBe('stub')
    expect(row?.tags).toEqual(['t-lost'])
    expect(row?.startedAt).toBe(lost.startedAt)
    expect(row?.model).toBe('qwen2.5:1.5b')

    rendered.unmount()
  })

  // K0b/R27 — the descriptor is authoritative for TTFT, the same way it already was for model:
  // the server records it in the same locked descriptor it publishes `first_token` from, so a
  // row rebuilt from the recovery snapshot carries the live TTFT the server already measured,
  // physically consistent with the frame this client received before the reconnect.
  it('keeps a known TTFT on a row rebuilt from the recovery snapshot', async () => {
    const { rendered } = setup({ listFetch: async () => listPage([]) })
    await waitFor(() => expect(FakeEventSource.latest()).toBeDefined())

    serverActive({ active: [activeDescriptor(1, summary(1), null, 42)], logPosition: 9 })

    act(() => {
      FakeEventSource.latest().open()
      FakeEventSource.latest().emit('started', startedEvent(1, summary(1)), 1)
      FakeEventSource.latest().emit('first_token', { seq: 1, ttftMs: 42 }, 2)
    })
    await waitFor(() => expect(rendered.result.current.live.inFlight[0]?.ttftMs).toBe(42))

    act(() => {
      FakeEventSource.latest().open() // reconnect → recovery rebuilds the row
    })
    await settle()

    expect(inFlightSeqs(rendered)).toEqual([1])
    expect(rendered.result.current.live.inFlight[0].ttftMs).toBe(42)

    rendered.unmount()
  })

  // R27 — the review's dropped-`first_token` sequence: the frame carrying the live TTFT never
  // reaches this client at all (dropped by the bounded queue), so before this fix nothing in
  // the client's own state could ever restore it. The descriptor is now the *only* source, and
  // it must still show the already-measured 42 ms.
  it('recovers a TTFT whose first_token frame was dropped entirely', async () => {
    const { rendered } = setup({ listFetch: async () => listPage([]) })
    await waitFor(() => expect(FakeEventSource.latest()).toBeDefined())

    // Frame 1 (started, seq 2) reaches the client and sets the watermark; frame 2
    // (first_token, seq 2, ttftMs=42) is dropped by the bounded queue; frame 3 (an unrelated
    // started, seq 3) exposes the gap while request 2 is still running. The server measured
    // and kept the TTFT in seq 2's locked descriptor regardless of the drop.
    serverActive({
      active: [activeDescriptor(2, summary(2), null, 42), activeDescriptor(3, summary(3))],
      logPosition: 3,
    })

    act(() => {
      FakeEventSource.latest().open()
      FakeEventSource.latest().emit('hello', { serverRunId: 'run-1' })
      FakeEventSource.latest().emit('started', startedEvent(2, summary(2)), 1)
      FakeEventSource.latest().emit('started', startedEvent(3, summary(3)), 3)
    })

    await settle()

    const row = rendered.result.current.live.inFlight.find((r) => r.seq === 2)
    expect(row).toBeDefined()
    expect(row?.ttftMs).toBe(42)

    rendered.unmount()
  })

  // R23 (I0a's review order B, retained) — the `cleared` frame is dropped by the bounded
  // drop-oldest queue, so the client never sees it; the loss shows up only as an id gap.
  // Recovery must not depend on that frame: it discards the completion buffer wholesale and
  // refetches, and the refetch reads the post-clear database.
  it('applies a clear it never saw, from the recovery snapshot, to the completion buffer', async () => {
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

    serverActive({ active: [], logPosition: 4 })

    // A later frame reveals the gap and triggers recovery.
    act(() => {
      FakeEventSource.latest().emit('started', startedEvent(2, summary(2)), 5)
    })

    await settle()

    // History resolves authoritatively empty; the buffer must not remerge the deleted row.
    await act(async () => {
      resolveFetch!(listPage([]))
      await new Promise((r) => setTimeout(r, 50))
    })

    expect(cachedRowIds(queryClient)).toEqual([])

    rendered.unmount()
  })

  // ---- the model's claim, tested as a property -------------------------------------------

  /**
   * J0 claims *order-independence at settlement*: whatever order frames arrive in, and
   * whichever are lost, once loss is detected and recovery has settled the client's state
   * equals the server's. This drives a small simulated server — a frame script with a real log
   * position, a database that reflects the frames published so far, and snapshots taken at the
   * tightest legal position — through randomised delivery orders and losses, then asserts
   * exactly that. Examples pin the four review orderings; this pins the claim they came from.
   *
   * Deterministic PRNG: a failure has to be reproducible from its seed.
   */
  function prng(seed: number) {
    let state = seed >>> 0
    return () => {
      state = (state * 1664525 + 1013904223) >>> 0
      return state / 0x100000000
    }
  }

  type Frame =
    | { id: number; kind: 'started'; seq: number; row: Summary }
    | { id: number; kind: 'completed'; seq: number; row: Summary }
    | { id: number; kind: 'cleared' }

  /** Build one scenario: N requests, some left running, with a clear somewhere in the middle. */
  function scenario(rand: () => number) {
    const frames: Frame[] = []
    const stillRunning = new Set<number>()
    let id = 0
    let seq = 0
    let rowId = 0

    const requests = 4 + Math.floor(rand() * 4)
    const clearAfter = Math.floor(rand() * requests)

    for (let i = 0; i < requests; i++) {
      seq++
      // The last request always runs on, and its `started` frame is always lost below — so
      // every scenario, not just the lucky seeds, exercises the half of convergence round six
      // found missing: a request the server reports active that this client never saw start.
      const runs = rand() < 0.25 || i === requests - 1
      const row = summary(Math.min(9, ++rowId), { path: `/api/chat?seq=${seq}` })
      frames.push({ id: ++id, kind: 'started', seq, row })
      if (runs) {
        stillRunning.add(seq)
      } else {
        frames.push({ id: ++id, kind: 'completed', seq, row })
      }

      if (i === clearAfter) {
        frames.push({ id: ++id, kind: 'cleared' })
        rowId = 0 // a clear-all empties the table, so SQLite restarts row ids (R14b)
      }
    }

    /**
     * The server's in-flight set as of `position`, as descriptors (K0b) — started, not yet
     * completed. The registry is filled at registration, so the server can describe a request
     * whether or not its `started` frame ever reached this client.
     */
    const activeAt = (position: number) => {
      const active = new Map<number, ActiveDescriptor>()
      for (const f of frames) {
        if (f.id > position) break
        if (f.kind === 'started') active.set(f.seq, activeDescriptor(f.seq, f.row))
        if (f.kind === 'completed') active.delete(f.seq)
      }

      return [...active.values()]
    }

    /** The rows a list read would return at `position`: inserted since the last clear. */
    const rowsAt = (position: number) => {
      let rows: Summary[] = []
      for (const f of frames) {
        if (f.id > position) break
        if (f.kind === 'cleared') rows = []
        // `completed` is published after the insert, so its row is in the database by then.
        if (f.kind === 'completed') rows = [f.row, ...rows]
      }

      return rows
    }

    return { frames, stillRunning, lostStartSeq: seq, lastId: id, activeAt, rowsAt }
  }

  for (const seed of [1, 7, 13, 21, 42, 99]) {
    it(`converges on the server's settled state under randomised delivery (seed ${seed})`, async () => {
      const rand = prng(seed)
      const world = scenario(rand)

      // The database the client can read is whatever the server has published so far.
      let position = 0
      const listFetch = () => Promise.resolve(listPage(world.rowsAt(position)))
      // Snapshots are taken at the tightest position the server could honestly report: at
      // least everything the client has already been sent.
      vi.spyOn(api, 'getActiveRequests').mockImplementation(async () => ({
        active: world.activeAt(position),
        logPosition: position,
        serverRunId: 'run-1',
      }))

      const { queryClient, rendered } = setup({ listFetch })
      await waitFor(() => expect(FakeEventSource.latest()).toBeDefined())
      act(() => {
        FakeEventSource.latest().open()
        FakeEventSource.latest().emit('hello', { serverRunId: 'run-1' })
      })

      // Deliver in a shuffled order, dropping some frames outright — a bounded drop-oldest
      // queue loses whatever it loses, and the client is only promised detection, not order.
      const delivery = [...world.frames]
      for (let i = delivery.length - 1; i > 0; i--) {
        const j = Math.floor(rand() * (i + 1))
        ;[delivery[i], delivery[j]] = [delivery[j], delivery[i]]
      }

      const half = Math.ceil(delivery.length / 2)
      for (let i = 0; i < delivery.length; i++) {
        const frame = delivery[i]
        const lostStart = frame.kind === 'started' && frame.seq === world.lostStartSeq
        if (lostStart || rand() < 0.25) {
          // Dropped by the subscriber's bounded queue; the server still published it.
          position = Math.max(position, frame.id)
          continue
        }

        act(() => {
          const source = FakeEventSource.latest()
          position = Math.max(position, frame.id)
          if (frame.kind === 'started') source.emit('started', startedEvent(frame.seq, frame.row), frame.id)
          else if (frame.kind === 'completed') source.emit('completed', { seq: frame.seq, row: frame.row }, frame.id)
          else source.emit('cleared', {}, frame.id)
        })

        // A reconnect in the middle: recovery has to compose with frames still arriving.
        if (i === half) {
          await settle(120)
          act(() => FakeEventSource.latest().open())
        }
      }

      // Everything is published; the run ends with a reconnect, which is what makes undetected
      // loss detectable at all. Settled state must then equal the server's.
      position = world.lastId
      act(() => FakeEventSource.latest().open())
      await settle(500)

      expect(cachedRowIds(queryClient)).toEqual(world.rowsAt(world.lastId).map((r) => r.id))
      // K2 — convergence is two-sided, and the second half is the one round six found missing:
      // not just "nothing is shown that isn't running" (no false positives), but "everything
      // running is shown" (no false negatives), including requests whose `started` frame this
      // delivery order dropped entirely.
      expect(inFlightSeqs(rendered)).toEqual([...world.stillRunning].sort((a, b) => a - b))

      rendered.unmount()
    })
  }
})
