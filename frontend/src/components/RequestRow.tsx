import type { Summary } from '@/api/types'
import type { InFlightRequest } from '@/api/useEvents'
import { Badge } from '@/components/ui/badge'
import { cn } from '@/lib/utils'
import { formatMs, formatTokPerSec, splitMetric, truncateMiddle } from '@/lib/format'
import { tagVariant } from '@/lib/tags'

const MAX_ROW_TAGS = 2

/** §5.1 — two lines, 8×12 padding, mono method chip + mono path (middle-truncated), fixed-width metric sub-columns. */
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
  const shownTags = row.tags.slice(0, MAX_ROW_TAGS)
  const overflowTags = row.tags.length - shownTags.length

  return (
    <button
      type="button"
      onClick={() => onSelect(row.id)}
      className={cn(
        'relative flex w-full flex-col gap-1 border-b border-border px-3 py-2 text-left text-xs hover:bg-surface-2',
        selected && 'bg-surface-3',
      )}
    >
      {selected && <span className="absolute left-0 top-0 h-full w-0.5 bg-accent" aria-hidden="true" />}
      <div className="flex items-center gap-2">
        <StatusDot error={isError} />
        <span className="shrink-0 rounded-chip bg-surface-2 px-1.5 py-0.5 font-mono text-xs text-text-secondary">
          {row.method}
        </span>
        <span className="min-w-0 flex-1 truncate font-mono text-sm text-text">{truncateMiddle(row.path)}</span>
        <span className="ml-auto flex shrink-0 items-center font-mono text-xs tabular-nums">
          <MetricCell formatted={formatMs(row.durationMs)} width="w-14" />
          <MetricCell formatted={formatTokPerSec(row.tokPerSec)} width="w-16" />
        </span>
      </div>
      <div className="flex items-center gap-1.5 overflow-hidden pl-[18px]">
        {shownTags.length > 0 ? (
          <>
            {shownTags.map((t) => (
              <Badge key={t} variant={tagVariant(t)} className="shrink-0">
                {t}
              </Badge>
            ))}
            {overflowTags > 0 && (
              <Badge variant="neutral" className="shrink-0">
                +{overflowTags}
              </Badge>
            )}
            {row.model && <span className="shrink truncate font-mono text-xs text-text-muted">{row.model}</span>}
          </>
        ) : (
          row.model && <span className="shrink truncate font-mono text-xs text-text-muted">{row.model}</span>
        )}
        {/* One right-aligned group: `ml-auto` on each badge would split the spare space
            between them, floating the warning count at a different x on every row. */}
        <span className="ml-auto flex shrink-0 gap-1.5">
          {row.warnings.length > 0 && (
            <Badge variant={isError ? 'danger' : 'warn'}>{row.warnings.length}</Badge>
          )}
          {row.replayOf != null && <Badge variant="neutral">replay #{row.replayOf}</Badge>}
        </span>
      </div>
    </button>
  )
}

/**
 * §5.1 + ui-spec.md §9.1 (in-flight review TODO) — clickable, like a completed row; a
 * lightweight client-side detail (InFlightDetailPane) has everything already in `item`.
 * Line 2 matches the completed-row anatomy (tags lead, model follows in muted text) —
 * but the model slot stays empty until `request_ready` lands (the `started` event that
 * creates this row fires before the request body is read, so it never carries one).
 * Backend gets its own Badge, not plain text in the model slot — the row used to render
 * the backend name where a model normally goes, reading as "the model is called ollama".
 */
export function InFlightRow({
  item,
  now,
  selected,
  onSelect,
}: {
  item: InFlightRequest
  now: number
  selected: boolean
  onSelect: (seq: number) => void
}) {
  const elapsedMs = now - Date.parse(item.startedAt)
  const shownTags = item.tags.slice(0, MAX_ROW_TAGS)
  const overflowTags = item.tags.length - shownTags.length

  return (
    <button
      type="button"
      onClick={() => onSelect(item.seq)}
      className={cn(
        'relative flex w-full flex-col gap-1 border-b border-border bg-[color-mix(in_srgb,var(--color-accent)_5%,transparent)] px-3 py-2 text-left text-xs hover:bg-[color-mix(in_srgb,var(--color-accent)_10%,transparent)]',
        selected && 'bg-surface-3',
      )}
    >
      {selected && <span className="absolute left-0 top-0 h-full w-0.5 bg-accent" aria-hidden="true" />}
      <div className="flex items-center gap-2">
        <span className="pulse-dot h-2 w-2 shrink-0 rounded-full bg-accent" />
        <span className="truncate font-mono text-sm text-text">{item.method} {truncateMiddle(item.path)}</span>
        <span className="ml-auto shrink-0 font-mono text-xs tabular-nums text-text-muted">{formatMs(elapsedMs)}</span>
      </div>
      <div className="flex items-center gap-1.5 pl-[18px]">
        {shownTags.map((t) => (
          <Badge key={t} variant={tagVariant(t)} className="shrink-0">
            {t}
          </Badge>
        ))}
        {overflowTags > 0 && (
          <Badge variant="neutral" className="shrink-0">
            +{overflowTags}
          </Badge>
        )}
        {item.model && <span className="shrink truncate font-mono text-xs text-text-muted">{item.model}</span>}
        <Badge variant="neutral" className="shrink-0">
          {item.backend}
        </Badge>
        {item.replayOf != null && <Badge variant="neutral">replay #{item.replayOf}</Badge>}
        {item.ttftMs != null && (
          <span className="ml-auto shrink-0 font-mono text-xs text-text-secondary">TTFT {formatMs(item.ttftMs)}</span>
        )}
      </div>
    </button>
  )
}

/** §5.1 — duration/tok-s render as fixed-width right-aligned sub-columns so digits align down the list; the unit dims, the digits carry the emphasis. */
function MetricCell({ formatted, width }: { formatted: string; width: string }) {
  const [value, unit] = splitMetric(formatted)
  return (
    <span className={cn('shrink-0 text-right text-text-secondary', width)}>
      {value}
      {unit && <span className="text-text-muted">{unit}</span>}
    </span>
  )
}

function StatusDot({ error }: { error: boolean }) {
  return <span className={cn('h-2 w-2 shrink-0 rounded-full', error ? 'bg-danger' : 'bg-ok')} />
}
