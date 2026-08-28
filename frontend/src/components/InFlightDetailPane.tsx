import type { InFlightRequest } from '@/api/useEvents'
import { Badge } from '@/components/ui/badge'
import { Mark } from '@/components/ui/Mark'
import { CardGrid, MetricCard, SectionLabel } from '@/components/DetailPane'
import { formatMs, formatTimestamp } from '@/lib/format'
import { tagVariant } from '@/lib/tags'

/**
 * ui-spec.md §9.1 (in-flight review TODO) — a lightweight, client-side detail for a
 * selected in-flight row: everything here already lives in the `inFlight` map (§D5/D6),
 * so there's no REST fetch. Deliberately not the full Overview/Request/Response/Headers
 * tab set `DetailPane` has — a request that hasn't completed has no response to show yet.
 * A live response tail is explicitly out of scope (touches the proxy hot path; Phase 5).
 */
export function InFlightDetailPane({ item, now }: { item: InFlightRequest | null; now: number }) {
  if (!item) {
    return (
      <div className="flex h-full flex-col items-center justify-center gap-2 p-6 text-center">
        <Mark size={28} muted />
        <p className="text-sm text-text-muted">This request just completed — pick it up from the list.</p>
      </div>
    )
  }

  const elapsedMs = now - Date.parse(item.startedAt)
  const state = item.ttftMs != null ? `streaming — TTFT ${formatMs(item.ttftMs)}` : 'waiting for first token…'

  return (
    <div className="flex h-full flex-col">
      <div className="flex items-center gap-2 border-b border-border px-3 py-2">
        <span className="pulse-dot h-2 w-2 shrink-0 rounded-full bg-accent" aria-hidden="true" />
        <span className="text-sm font-medium text-text">In flight</span>
      </div>
      <div className="min-h-0 flex-1 overflow-y-auto p-3">
        <div className="flex flex-col gap-4 text-sm">
          <div>
            <div className="font-mono font-medium text-text">
              {item.method} {item.path}
            </div>
            <div className="text-text-muted">{formatTimestamp(item.startedAt)}</div>
          </div>

          <div>
            <SectionLabel>Status</SectionLabel>
            <CardGrid>
              <MetricCard label="State" value={state} />
              <MetricCard label="Elapsed" value={formatMs(elapsedMs)} />
              <MetricCard label="Backend" value={item.backend} />
              <MetricCard label="Model" value={item.model ?? '—'} />
            </CardGrid>
          </div>

          {item.tags.length > 0 && (
            <div>
              <SectionLabel>Tags</SectionLabel>
              <div className="flex flex-wrap gap-1.5">
                {item.tags.map((t) => (
                  <Badge key={t} variant={tagVariant(t)}>
                    {t}
                  </Badge>
                ))}
              </div>
            </div>
          )}
        </div>
      </div>
    </div>
  )
}
