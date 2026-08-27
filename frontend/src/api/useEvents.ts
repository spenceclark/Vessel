import { useEffect, useRef, useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import type { CompletedEvent, FirstTokenEvent, StartedEvent, Summary } from './types'

export interface InFlightRequest {
  seq: number
  startedAt: string
  method: string
  path: string
  backend: string
  tags: string[]
  ttftMs?: number
}

/**
 * D5/D6 — subscribes to the SSE lifecycle feed and tracks in-flight requests: added on
 * `started`, given a live TTFT on `first_token`, removed on `completed` (whatever the
 * outcome — the completed row itself, success or drop, is handed to `onCompleted`).
 * A `Map` keyed by `seq` preserves arrival order for the caller's in-flight display.
 */
export function useEvents(onCompleted: (row: Summary | null, seq: number) => void) {
  const [inFlight, setInFlight] = useState<Map<number, InFlightRequest>>(new Map())
  const [connected, setConnected] = useState(false)
  const onCompletedRef = useRef(onCompleted)
  onCompletedRef.current = onCompleted
  const queryClient = useQueryClient()
  const hasConnectedBefore = useRef(false)

  useEffect(() => {
    const source = new EventSource('/vessel/api/events')

    // C2 — a dropped EventSource (laptop sleep, Vessel restart) loses whatever fired
    // during the gap. EventSource reconnects on its own; on every `open` after the
    // first, close the gap with a refetch instead of leaving the list stale.
    source.addEventListener('open', () => {
      setConnected(true)
      if (hasConnectedBefore.current) {
        void queryClient.invalidateQueries({ queryKey: ['requests'] })
        void queryClient.invalidateQueries({ queryKey: ['stats'] })
      }
      hasConnectedBefore.current = true
    })
    source.addEventListener('error', () => setConnected(false))

    source.addEventListener('started', (e: MessageEvent<string>) => {
      const data = JSON.parse(e.data) as StartedEvent
      setInFlight((prev) => {
        const next = new Map(prev)
        next.set(data.seq, { ...data })
        return next
      })
    })

    source.addEventListener('first_token', (e: MessageEvent<string>) => {
      const data = JSON.parse(e.data) as FirstTokenEvent
      setInFlight((prev) => {
        const existing = prev.get(data.seq)
        if (!existing) return prev
        const next = new Map(prev)
        next.set(data.seq, { ...existing, ttftMs: data.ttftMs })
        return next
      })
    })

    source.addEventListener('completed', (e: MessageEvent<string>) => {
      const data = JSON.parse(e.data) as CompletedEvent
      setInFlight((prev) => {
        if (!prev.has(data.seq)) return prev
        const next = new Map(prev)
        next.delete(data.seq)
        return next
      })
      onCompletedRef.current(data.row, data.seq)
    })

    return () => source.close()
  }, [])

  return { inFlight, connected }
}

/** D6 — one shared 250ms interval driving every in-flight row's running timer, not per-row. */
export function useNowTick(intervalMs = 250) {
  const [now, setNow] = useState(() => Date.now())

  useEffect(() => {
    const id = window.setInterval(() => setNow(Date.now()), intervalMs)
    return () => window.clearInterval(id)
  }, [intervalMs])

  return now
}
