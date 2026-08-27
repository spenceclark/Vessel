import type { Summary } from '@/api/types'
import type { InFlightRequest } from '@/api/useEvents'
import { Badge } from '@/components/ui/badge'
import { cn } from '@/lib/utils'
import { formatMs, formatTokPerSec } from '@/lib/format'

export function RequestRow({
  row,
  selected,
  onSelect,
}: {
  row: Summary
  selected: boolean
  onSelect: (id: number) => void
}) {
  const isError = row.error != null || (row.statusCode ?? 0) >= 400

  return (
    <button
      type="button"
      onClick={() => onSelect(row.id)}
      className={cn(
        'flex w-full flex-col gap-1 border-b border-[var(--border)] px-3 py-2 text-left text-xs hover:bg-[var(--card)]',
        selected && 'bg-[var(--card)]',
      )}
    >
      <div className="flex items-center gap-2">
        <StatusDot error={isError} />
        <span className="truncate font-medium">
          {row.method} {row.path}
        </span>
        {row.warnings.length > 0 && (
          <Badge variant={isError ? 'danger' : 'warning'} className="ml-auto shrink-0">
            {row.warnings.length}
          </Badge>
        )}
      </div>
      <div className="flex items-center gap-2 overflow-hidden text-[var(--muted)]">
        {row.model && <span className="truncate">{row.model}</span>}
        <span className="shrink-0">{formatMs(row.durationMs)}</span>
        {row.tokPerSec != null && <span className="shrink-0">{formatTokPerSec(row.tokPerSec)}</span>}
        {row.tags.map((t) => (
          <span key={t} className="shrink-0 rounded bg-[var(--card)] px-1.5 py-0.5">
            {t}
          </span>
        ))}
      </div>
    </button>
  )
}

export function InFlightRow({ item, now }: { item: InFlightRequest; now: number }) {
  const elapsedMs = now - Date.parse(item.startedAt)

  return (
    <div className="flex w-full flex-col gap-1 border-b border-[var(--border)] bg-[var(--accent)]/5 px-3 py-2 text-left text-xs">
      <div className="flex items-center gap-2">
        <span className="h-2 w-2 shrink-0 animate-pulse rounded-full bg-[var(--accent)]" />
        <span className="truncate font-medium">
          {item.method} {item.path}
        </span>
        <span className="ml-auto shrink-0 tabular-nums text-[var(--muted)]">{formatMs(elapsedMs)}</span>
      </div>
      <div className="flex items-center gap-2 text-[var(--muted)]">
        <span className="shrink-0">{item.backend}</span>
        {item.ttftMs != null && <span className="shrink-0">TTFT {formatMs(item.ttftMs)}</span>}
        {item.tags.map((t) => (
          <span key={t} className="shrink-0 rounded bg-[var(--card)] px-1.5 py-0.5">
            {t}
          </span>
        ))}
      </div>
    </div>
  )
}

function StatusDot({ error }: { error: boolean }) {
  return <span className={cn('h-2 w-2 shrink-0 rounded-full', error ? 'bg-[var(--danger)]' : 'bg-emerald-500')} />
}
