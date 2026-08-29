import { useEffect, useRef, useState } from 'react'
import type {
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
  replayOf: number | null
  model?: string
  ttftMs?: number
}

/**
 * J0 — every lifecycle handler receives its frame's SSE `id:` alongside the payload. That id is
 * the log position `GET /active` reports itself against, and it is the whole basis of recovery:
 * without it the consumer cannot tell which of the frames it is holding a snapshot already
 * accounts for. A frame with no usable id is reported as `Infinity` — "position unknown" — so a
 * consumer comparing against a position applies it rather than discarding it.
 */
export interface EventHandlers {
  onStarted: (data: StartedEvent, id: number) => void
  onRequestReady: (data: RequestReadyEvent, id: number) => void
  onFirstToken: (data: FirstTokenEvent, id: number) => void
  onCompleted: (data: CompletedEvent, id: number) => void
  /** H0a/R23/J0 — history was cleared at this position; the frame itself carries no payload. */
  onCleared: (id: number) => void
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

    /**
     * Decodes one frame, reporting any gap in the publish sequence before dispatching it with
     * its log position (J0).
     */
    function receive<T>(event: MessageEvent<string>, dispatch: (data: T, id: number) => void) {
      const parsed = Number(event.lastEventId)
      const usable = Number.isFinite(parsed) && parsed > 0
      if (usable) {
        if (lastId !== null && parsed > lastId + 1) {
          handlersRef.current.onGap(parsed - lastId - 1)
        }

        // R22 — only ever advance. The server now publishes ids in order (allocation and
        // fan-out share a lock), but a defensive floor here means a late lower id can never
        // rewind the watermark and manufacture a phantom gap on the *next* frame.
        if (lastId === null || parsed > lastId) {
          lastId = parsed
        }
      }

      // No usable id ⇒ no known position. Reported as Infinity so a consumer ordering frames
      // against a snapshot applies it, rather than dropping a real lifecycle change because a
      // comparison it cannot take part in went the wrong way.
      dispatch(JSON.parse(event.data) as T, usable ? parsed : Number.POSITIVE_INFINITY)
    }

    source.addEventListener('started', (e: MessageEvent<string>) =>
      receive<StartedEvent>(e, (data, id) => handlersRef.current.onStarted(data, id)),
    )
    source.addEventListener('request_ready', (e: MessageEvent<string>) =>
      receive<RequestReadyEvent>(e, (data, id) => handlersRef.current.onRequestReady(data, id)),
    )
    source.addEventListener('first_token', (e: MessageEvent<string>) =>
      receive<FirstTokenEvent>(e, (data, id) => handlersRef.current.onFirstToken(data, id)),
    )
    source.addEventListener('completed', (e: MessageEvent<string>) =>
      receive<CompletedEvent>(e, (data, id) => handlersRef.current.onCompleted(data, id)),
    )
    // `cleared` is a real published frame (it carries an `id:`), so it flows through `receive`
    // and participates in gap detection — a dropped clear is detectable like any other loss.
    // Its position is the only thing it carries; the payload is empty (J0).
    source.addEventListener('cleared', (e: MessageEvent<string>) =>
      receive<unknown>(e, (_data, id) => handlersRef.current.onCleared(id)),
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
