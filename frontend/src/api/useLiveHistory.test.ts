import { createElement, type ReactNode } from 'react'
import { QueryClient, QueryClientProvider, useInfiniteQuery } from '@tanstack/react-query'
import { act, renderHook, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { EMPTY_FILTERS, type ActiveRequestsResponse, type RequestListResponse, type Summary } from './types'
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

/** Stub the server's active-request set for the next reconciliation. */
function serverActive(active: ActiveRequestsResponse) {
  vi.spyOn(api, 'getActiveRequests').mockResolvedValue(active)
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
      .mockResolvedValue({ activeSeqs: [1, 2, 3, 4], newestCompletedSeq: 0 })
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

  // R23/F3 — the review's repro: a completion buffered during a pending fetch must not
  // restore a row a clear-all deleted before the buffer drained. Cache must be [], not [1].
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
    act(() => {
      FakeEventSource.latest().emit('completed', { seq: 1, row: summary(1) }, 1)
    })

    // A clear-all lands before the buffer drains.
    act(() => {
      rendered.result.current.live.notifyCleared({ all: true }, null)
    })

    // The (post-clear) fetch resolves empty; draining must not resurrect row 1.
    await act(async () => {
      resolveFetch!(listPage([]))
    })

    await new Promise((r) => setTimeout(r, 20))
    expect(cachedRowIds(queryClient)).toEqual([])

    rendered.unmount()
  })

  // R23/F3 — clear-before keeps buffered completions that finished *above* the boundary and
  // discards ones at or below it.
  it('keeps a buffered completion above the clear-before boundary and drops one below it', async () => {
    let resolveFetch: ((value: RequestListResponse) => void) | undefined
    const listFetch = () =>
      new Promise<RequestListResponse>((resolve) => {
        resolveFetch = resolve
      })

    const { queryClient, rendered } = setup({ listFetch })
    await waitFor(() => expect(resolveFetch).toBeDefined())
    await waitFor(() => expect(FakeEventSource.latest()).toBeDefined())

    // Two completions buffered during the pending fetch: an old row (id 2) and a newer one
    // (id 9).
    act(() => {
      FakeEventSource.latest().emit('completed', { seq: 1, row: summary(2) }, 1)
      FakeEventSource.latest().emit('completed', { seq: 2, row: summary(9) }, 2)
    })

    // Clear before, deleting up to id 5. Row 2 is removed; row 9 legitimately survives.
    act(() => {
      rendered.result.current.live.notifyCleared({ before: '2026-08-28T00:00:05.000Z' }, 5)
    })

    await act(async () => {
      resolveFetch!(listPage([]))
    })

    await waitFor(() => expect(cachedRowIds(queryClient)).toEqual([9]))
    expect(cachedRowIds(queryClient)).not.toContain(2)

    rendered.unmount()
  })

  // R23/F3 — a completion that arrives *after* the clear (new generation) is for a surviving
  // or new row and must merge normally.
  it('keeps a completion buffered after the clear generation bumped', async () => {
    let resolveFetch: ((value: RequestListResponse) => void) | undefined
    const listFetch = () =>
      new Promise<RequestListResponse>((resolve) => {
        resolveFetch = resolve
      })

    const { queryClient, rendered } = setup({ listFetch })
    await waitFor(() => expect(resolveFetch).toBeDefined())
    await waitFor(() => expect(FakeEventSource.latest()).toBeDefined())

    // Clear-all first…
    act(() => {
      rendered.result.current.live.notifyCleared({ all: true }, null)
    })
    // …then a completion for a genuinely new row arrives while the fetch is still pending.
    act(() => {
      FakeEventSource.latest().emit('completed', { seq: 5, row: summary(9) }, 5)
    })

    await act(async () => {
      resolveFetch!(listPage([]))
    })

    await waitFor(() => expect(cachedRowIds(queryClient)).toEqual([9]))

    rendered.unmount()
  })
})
