import { useCallback, useRef, useState } from 'react'

/**
 * Phase 7 D6 — measures the chart's container with a ResizeObserver. SVG is sized in real
 * pixels from the measured box (never scaled through a viewBox — scaling distorts stroke
 * widths and text), so the frame re-renders on container resize instead of the page.
 *
 * A callback ref, not a `useEffect` over a `useRef` object: `ChartFrame` only mounts its
 * measured div once `empty` is false, which can happen on a *later* render than the
 * component's first (the query resolves after mount, or a card's data flips empty→
 * populated→empty→populated again across a query-key change, e.g. switching the
 * context-growth group-by). A mount-once effect (`[]` deps) would have already run and
 * bailed out on `ref.current === null` by then and never fire again, leaving `size` stuck
 * at zero forever — the chart's `width > 0` render gate would never pass, so nothing ever
 * drew even though the sr-only data table had every row. A callback ref re-invokes on
 * every attach/detach, whichever render that happens on.
 *
 * The synchronous `getBoundingClientRect()` read on attach (not just `observer.observe`)
 * matters for the same reason: `ResizeObserver`'s *first* notification for a newly
 * observed target is itself asynchronous (queued for the next rendering opportunity), so
 * a fast re-mount — attach, then detach again before that first callback ever gets a
 * chance to fire — can disconnect the observer while `size` is still its stale zero from
 * the previous mount, with nothing left to ever correct it. Reading the box synchronously
 * at attach time means the very first paint already reflects the real size regardless of
 * whether the observer's own callback ever gets to run; the observer stays attached only
 * to catch genuine post-mount resizes.
 */
export function useChartSize(): [(node: HTMLDivElement | null) => void, { width: number; height: number }] {
  const [size, setSize] = useState({ width: 0, height: 0 })
  const observerRef = useRef<ResizeObserver | null>(null)

  const ref = useCallback((node: HTMLDivElement | null) => {
    observerRef.current?.disconnect()
    observerRef.current = null
    if (!node) return

    const rect = node.getBoundingClientRect()
    setSize((previous) =>
      previous.width === rect.width && previous.height === rect.height ? previous : { width: rect.width, height: rect.height },
    )

    const observer = new ResizeObserver((entries) => {
      const entry = entries[0]
      if (!entry) return
      const { width, height } = entry.contentRect
      setSize((previous) => (previous.width === width && previous.height === height ? previous : { width, height }))
    })
    observer.observe(node)
    observerRef.current = observer
  }, [])

  return [ref, size]
}
