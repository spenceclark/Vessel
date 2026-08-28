import { useQuery } from '@tanstack/react-query'
import { AlertTriangle } from 'lucide-react'
import { api } from '@/api/client'

/**
 * R06 (UI half) — the writer's terminal give-up state used to be visible only as a single
 * log line; traffic keeps proxying (that guarantee doesn't change), but nothing is being
 * recorded, and the difference between "nothing happened" and "nothing was captured" isn't
 * one a user should have to notice by absence. Shares the `['status']` query with
 * `StatsBar` (same key, same cache — no extra request), and is persistent by design: it
 * isn't dismissible, because the condition it reports doesn't go away until a restart.
 */
export function CaptureHealthBanner() {
  const statusQuery = useQuery({
    queryKey: ['status'],
    queryFn: api.getStatus,
    staleTime: 60_000,
  })

  const capture = statusQuery.data?.capture
  if (!capture || capture.recording) return null

  return (
    <div
      role="alert"
      className="flex items-center gap-2 rounded-panel border border-[color-mix(in_srgb,var(--color-danger)_40%,transparent)] bg-[color-mix(in_srgb,var(--color-danger)_10%,transparent)] px-4 py-2 text-sm text-danger"
    >
      <AlertTriangle className="h-4 w-4 shrink-0" strokeWidth={1.75} aria-hidden="true" />
      <span>
        Capture stopped — restart Vessel to resume recording. Traffic is still being proxied normally.
        {capture.stoppedReason ? ` (${capture.stoppedReason})` : ''}
      </span>
    </div>
  )
}
