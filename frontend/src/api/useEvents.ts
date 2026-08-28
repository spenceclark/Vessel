import { useEffect, useRef, useState } from 'react'
import type {
  ClearedEvent,
  CompletedEvent,
  FirstTokenEvent,
  HelloEvent,
  RequestReadyEvent,
  StartedEvent,
} from './types'

export interface InFlightRequest {
  seq: number
  startedAt: string
  sessionId: number
  method: string
  path: string
  backend: string
  tags: string[]
  model?: string
  ttftMs?: number
}

export interface EventHandlers {
  onStarted: (data: StartedEvent) => void
  onRequestReady: (data: RequestReadyEvent) => void
  onFirstToken: (data: FirstTokenEvent) => void
  onCompleted: (data: CompletedEvent) => void
  /** H0a/R23 — the server cleared history; purge matching buffered + listed rows. */
  onCleared: (data: ClearedEvent) => void
  /** H0b — the first frame of every connection: this process's run id (a change means a restart). */
  onHello: (data: HelloEvent) => void
  /** R11 — frames were dropped between the last one and this one; the caller must reconcile. */
  onGap: (missed: number) => void
  /** The EventSource reconnected after a drop: everything during the gap was missed. */
  onReconnect: () => void
}

/**
 * D5/D6 — the SSE subscription, and nothing else: it decodes frames, tracks connectivity,
 * and reports loss. All state derived from these events (the in-flight map, cache merging,
 * reconciliation) lives in `useLiveHistory`, so this stays testable with a fake EventSource
 * and has no opinion about React Query.
 *
 * R11 — every frame carries a monotonic `id`. A jump means this subscriber's bounded
 * queue dropped frames (drop-oldest is deliberate: a stalled browser must never
 * back-pressure the request path), which is the only reliable signal that a `completed`
 * may have been lost. Without it a dropped completion left an in-flight row running
 * forever, with no way for the client to know.
 */
export function useEvents(handlers: EventHandlers) {
  const [connected, setConnected] = useState(false)

  // Installed once; re-reading through a ref keeps the subscription stable across renders
  // (reconnecting on every render would itself lose events). Assigned in an effect rather
  // than during render — SSE callbacks only ever fire asynchronously, so the effect has
  // always run by the time one does.
  const handlersRef = useRef(handlers)
  useEffect(() => {
    handlersRef.current = handlers
  })

  useEffect(() => {
    const source = new EventSource('/vessel/api/events')
    let lastId: number | null = null
    let hasConnectedBefore = false

    source.addEventListener('open', () => {
      setConnected(true)
      if (hasConnectedBefore) {
        // Whatever happened during the gap was missed, and ids restart relative to a new
        // server process — reset rather than reporting a spurious gap on the next frame.
        lastId = null
        handlersRef.current.onReconnect()
      }

      hasConnectedBefore = true
    })

    source.addEventListener('error', () => setConnected(false))

    /** Decodes one frame, reporting any gap in the publish sequence before dispatching it. */
    function receive<T>(event: MessageEvent<string>, dispatch: (data: T) => void) {
      const id = Number(event.lastEventId)
      if (Number.isFinite(id) && id > 0) {
        if (lastId !== null && id > lastId + 1) {
          handlersRef.current.onGap(id - lastId - 1)
        }

        // R22 — only ever advance. The server now publishes ids in order (allocation and
        // fan-out share a lock), but a defensive floor here means a late lower id can never
        // rewind the watermark and manufacture a phantom gap on the *next* frame.
        if (lastId === null || id > lastId) {
          lastId = id
        }
      }

      dispatch(JSON.parse(event.data) as T)
    }

    source.addEventListener('started', (e: MessageEvent<string>) =>
      receive<StartedEvent>(e, (data) => handlersRef.current.onStarted(data)),
    )
    source.addEventListener('request_ready', (e: MessageEvent<string>) =>
      receive<RequestReadyEvent>(e, (data) => handlersRef.current.onRequestReady(data)),
    )
    source.addEventListener('first_token', (e: MessageEvent<string>) =>
      receive<FirstTokenEvent>(e, (data) => handlersRef.current.onFirstToken(data)),
    )
    source.addEventListener('completed', (e: MessageEvent<string>) =>
      receive<CompletedEvent>(e, (data) => handlersRef.current.onCompleted(data)),
    )
    // `cleared` is a real published frame (it carries an `id:`), so it flows through `receive`
    // and participates in gap detection — a dropped clear is detectable like any other loss.
    source.addEventListener('cleared', (e: MessageEvent<string>) =>
      receive<ClearedEvent>(e, (data) => handlersRef.current.onCleared(data)),
    )
    // `hello` deliberately carries no `id:` (see the server), so it must NOT go through
    // `receive` — it is server identity, not a lifecycle frame, and must never move the gap
    // watermark.
    source.addEventListener('hello', (e: MessageEvent<string>) =>
      handlersRef.current.onHello(JSON.parse(e.data) as HelloEvent),
    )

    return () => source.close()
  }, [])

  return { connected }
}

/**
 * D6 — a running clock for in-flight rows' elapsed-time display. Each consumer owns its
 * own instance (R04, review §4 risk): a single top-level tick shared via props used to
 * rerender the entire app tree every 250ms, including panels with nothing time-sensitive
 * in them. `enabled` lets a consumer stop the interval entirely when it has nothing
 * in-flight to animate, rather than ticking (and rerendering) for no visible effect.
 */
export function useNowTick(intervalMs = 250, enabled = true) {
  const [now, setNow] = useState(() => Date.now())

  useEffect(() => {
    if (!enabled) return
    const id = window.setInterval(() => setNow(Date.now()), intervalMs)
    return () => window.clearInterval(id)
  }, [intervalMs, enabled])

  return now
}
