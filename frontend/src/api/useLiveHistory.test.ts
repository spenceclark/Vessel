import { createElement, type ReactNode } from 'react'
import { QueryClient, QueryClientProvider, useInfiniteQuery } from '@tanstack/react-query'
import { act, renderHook, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { EMPTY_FILTERS, type RequestListResponse, type Summary } from './types'
import { requestsQueryKey } from './queryKeys'
import { useLiveHistory } from './useLiveHistory'

/**
 * R10/R11/D05 — the reconciliation model, exercised at the seam where its failures
 * actually happen: a completion racing an unsettled list fetch, and lost SSE frames
 * leaving in-flight rows that never clear. Both were invisible to `tsc` and to the backend
 * suite, and both were reproduced by the review only under real timing.
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

let originalEventSource: unknown

beforeEach(() => {
  FakeEventSource.instances = []
  originalEventSource = (globalThis as Record<string, unknown>).EventSource
  ;(globalThis as Record<string, unknown>).EventSource = FakeEventSource
})

afterEach(() => {
  ;(globalThis as Record<string, unknown>).EventSource = originalEventSource
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

  // R11 — the review's stuck-rows case. A dropped `completed` (bounded drop-oldest queues)
  // leaves a row running forever unless the loss is *detectable*: the gap in the publish
  // id triggers reconciliation, and the refreshed history accounts for the row.
  it('clears an in-flight row whose completion was dropped, via the event-id gap', async () => {
    const row = summary(7)
    let rowsToReturn: Summary[] = []
    const { rendered } = setup({ listFetch: async () => listPage(rowsToReturn) })

    await waitFor(() => expect(FakeEventSource.latest()).toBeDefined())

    act(() => {
      FakeEventSource.latest().emit('started', startedEvent(1, row), 1)
    })
    await waitFor(() => expect(rendered.result.current.live.inFlight).toHaveLength(1))

    // The request finished and was stored, but its `completed` frame was dropped: the next
    // frame the client sees jumps the publish id.
    rowsToReturn = [row]
    act(() => {
      FakeEventSource.latest().emit('started', startedEvent(2, summary(8)), 9)
    })

    await waitFor(() => {
      expect(rendered.result.current.live.inFlight.map((i) => i.seq)).toEqual([2])
    })

    rendered.unmount()
  })

  // The other half of R11: a genuinely long-running request must survive reconciliation.
  // This is why the gap is detected from the publish id rather than from a distance on the
  // request `seq` — seq is assigned at start, so a slow request legitimately trails.
  it('keeps a long-running in-flight row that the refreshed history does not account for', async () => {
    const slow = summary(7)
    let rowsToReturn: Summary[] = []
    const { rendered } = setup({ listFetch: async () => listPage(rowsToReturn) })

    await waitFor(() => expect(FakeEventSource.latest()).toBeDefined())

    act(() => {
      FakeEventSource.latest().emit('started', startedEvent(1, slow), 1)
    })
    await waitFor(() => expect(rendered.result.current.live.inFlight).toHaveLength(1))

    // Many other requests complete; the slow one is still running, so it is absent from
    // history. A gap forces reconciliation anyway.
    rowsToReturn = [summary(8), summary(9)]
    act(() => {
      FakeEventSource.latest().emit('started', startedEvent(2, summary(10)), 500)
    })

    await waitFor(() => expect(rendered.result.current.live.inFlight).toHaveLength(2))
    expect(rendered.result.current.live.inFlight.map((i) => i.seq)).toContain(1)

    rendered.unmount()
  })

  // R11 — reconnect misses everything during the gap, so it reconciles unconditionally.
  it('reconciles after an EventSource reconnect', async () => {
    const row = summary(7)
    let rowsToReturn: Summary[] = []
    const { rendered } = setup({ listFetch: async () => listPage(rowsToReturn) })

    await waitFor(() => expect(FakeEventSource.latest()).toBeDefined())
    act(() => {
      FakeEventSource.latest().open() // first connect
      FakeEventSource.latest().emit('started', startedEvent(1, row), 1)
    })
    await waitFor(() => expect(rendered.result.current.live.inFlight).toHaveLength(1))

    rowsToReturn = [row]
    act(() => {
      FakeEventSource.latest().open() // reconnect
    })

    await waitFor(() => expect(rendered.result.current.live.inFlight).toHaveLength(0))

    rendered.unmount()
  })
})
