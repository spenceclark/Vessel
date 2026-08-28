import { useEffect, useRef, useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import type { CompletedEvent, FacetsResponse, FirstTokenEvent, RequestReadyEvent, StartedEvent, Summary } from './types'

export interface InFlightRequest {
  seq: number
  startedAt: string
  method: string
  path: string
  backend: string
  tags: string[]
  model?: string
  ttftMs?: number
}

/**
 * D5/D6 — subscribes to the SSE lifecycle feed and tracks in-flight requests: added on
 * `started`, given the real model on `request_ready` (post-Phase-4 — `started` fires
 * before the request body is read, so it never carries one) and a live TTFT on
 * `first_token`, removed on `completed` (whatever the outcome — the completed row
 * itself, success or drop, is handed to `onCompleted`). A `Map` keyed by `seq` preserves
 * arrival order for the caller's in-flight display.
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

    source.addEventListener('request_ready', (e: MessageEvent<string>) => {
      const data = JSON.parse(e.data) as RequestReadyEvent
      setInFlight((prev) => {
        const existing = prev.get(data.seq)
        if (!existing) return prev
        const next = new Map(prev)
        next.set(data.seq, { ...existing, model: data.model })
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

      // A completed row can carry a tag/model/backend/format the filter bar's cached
      // facets don't know about yet — without this, the "Tags:" picker (and the other
      // filter dropdowns) only pick it up after something else happens to refetch
      // (a scope toggle, a reload). Only invalidate the cache entries actually missing
      // something, checked against what's cached rather than refetched unconditionally,
      // so ordinary traffic doesn't hammer the facets endpoint on every completion.
      if (data.row) {
        for (const [key, cached] of queryClient.getQueriesData<FacetsResponse>({ queryKey: ['facets'] })) {
          if (introducesNewFacet(data.row, cached)) {
            void queryClient.invalidateQueries({ queryKey: key })
          }
        }
      }

      onCompletedRef.current(data.row, data.seq)
    })

    return () => source.close()
  }, [])

  return { inFlight, connected }
}

/** True when `row` has a tag/model/backend/format not already present in `cached` (undefined = nothing cached yet, nothing to catch up). */
function introducesNewFacet(row: Summary, cached: FacetsResponse | undefined): boolean {
  if (!cached) return false
  return (
    !cached.backends.includes(row.backend) ||
    !cached.formats.includes(row.format) ||
    (row.model != null && !cached.models.includes(row.model)) ||
    row.tags.some((t) => !cached.tags.includes(t))
  )
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
