import { X } from 'lucide-react'
import { EMPTY_FILTERS, filtersActive, type RequestFilters } from '@/api/types'
import { tagChipClass } from '@/lib/tags'

/**
 * Phase 7 D11 — because the FilterBar is not on screen in Reports, the active filters
 * render here as visible, individually-clearable chips; a silently-filtered chart is a
 * lie. Clearing mutates the same filters state App owns, so switching back to History
 * shows exactly the scope the charts described. With no active filters the bar renders
 * the session name alone, not an empty strip.
 */
export function ReportScopeBar({
  sessionLabel,
  filters,
  onFiltersChange,
}: {
  sessionLabel: string
  filters: RequestFilters
  onFiltersChange: (next: RequestFilters) => void
}) {
  if (!filtersActive(filters)) {
    return (
      <div className="flex items-center gap-2 text-xs text-text-muted" aria-label="Report scope">
        <span className="font-[550] text-text-secondary">{sessionLabel}</span>
      </div>
    )
  }

  const chips: { key: keyof RequestFilters; label: string; tag?: boolean }[] = []
  if (filters.q) chips.push({ key: 'q', label: `"${filters.q}"` })
  if (filters.backend) chips.push({ key: 'backend', label: `backend: ${filters.backend}` })
  if (filters.model) chips.push({ key: 'model', label: `model: ${filters.model}` })
  if (filters.format) chips.push({ key: 'format', label: `format: ${filters.format}` })
  if (filters.tag) chips.push({ key: 'tag', label: `tag: ${filters.tag}`, tag: true })
  if (filters.status !== 'all') chips.push({ key: 'status', label: `status: ${filters.status}` })
  if (filters.warnedOnly) chips.push({ key: 'warnedOnly', label: 'warnings only' })

  function clearChip(key: keyof RequestFilters) {
    onFiltersChange({ ...filters, [key]: key === 'q' ? '' : key === 'status' ? 'all' : key === 'warnedOnly' ? false : null })
  }

  return (
    <div className="flex flex-wrap items-center gap-1.5" aria-label="Report scope">
      <span className="text-xs font-[550] text-text-secondary">{sessionLabel}</span>
      {chips.map((chip) => (
        <span
          key={chip.key}
          className={`inline-flex h-6 items-center gap-1 rounded-full px-2.5 text-xs ${
            chip.tag ? tagChipClass(filters.tag ?? '') : 'bg-surface-3 text-text-secondary'
          }`}
        >
          {chip.label}
          <button
            type="button"
            aria-label={`Clear filter ${chip.label}`}
            className="-mr-1 rounded-full p-0.5 text-text-muted hover:text-text"
            onClick={() => clearChip(chip.key)}
          >
            <X className="h-3 w-3" strokeWidth={1.75} />
          </button>
        </span>
      ))}
      <button
        type="button"
        className="rounded-chip px-1.5 py-0.5 text-xs text-text-muted underline-offset-2 hover:text-text hover:underline"
        onClick={() => onFiltersChange({ ...EMPTY_FILTERS })}
      >
        Clear filters
      </button>
    </div>
  )
}