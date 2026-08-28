import { useEffect, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Search } from 'lucide-react'
import { api } from '@/api/client'
import { EMPTY_FILTERS, filtersActive, type RequestFilters, type SessionScope } from '@/api/types'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Tabs, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { tagChipClass } from '@/lib/tags'
import { cn } from '@/lib/utils'

const SELECT_CLASS = 'h-7 rounded-control border border-border bg-surface-2 px-1.5 text-xs text-text'

/**
 * D3 — the list panel's own header: debounced free-text search, backend/model/format
 * dropdowns from facets (hidden when a facet has ≤1 value), a tag chip picker, a status
 * toggle, and a warnings-only toggle. Filter state lives in the parent (App) so
 * RequestList's query key can include it.
 */
export function FilterBar({
  scope,
  filters,
  onFiltersChange,
}: {
  scope: SessionScope | null
  filters: RequestFilters
  onFiltersChange: (next: RequestFilters) => void
}) {
  const [qInput, setQInput] = useState(filters.q)

  // Keep the local text box in sync when the filter is cleared/changed externally
  // (e.g. "clear filters", or a chip's individual "x").
  useEffect(() => {
    setQInput(filters.q)
  }, [filters.q])

  useEffect(() => {
    if (qInput === filters.q) return
    const id = window.setTimeout(() => onFiltersChange({ ...filters, q: qInput }), 300)
    return () => window.clearTimeout(id)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [qInput])

  const facetsQuery = useQuery({
    queryKey: ['facets', scope],
    queryFn: () => api.getFacets(scope ?? undefined),
    enabled: scope !== null,
  })
  const facets = facetsQuery.data

  function set<K extends keyof RequestFilters>(key: K, value: RequestFilters[K]) {
    onFiltersChange({ ...filters, [key]: value })
  }

  const active = filtersActive(filters)

  return (
    <div className="flex flex-col gap-2 border-b border-border px-3 py-2.5">
      <div className="flex flex-wrap items-center gap-2">
        <Input
          type="text"
          value={qInput}
          onChange={(e) => setQInput(e.target.value)}
          placeholder="Search prompts & responses…"
          icon={<Search strokeWidth={1.75} />}
          className="min-w-[200px] flex-1"
        />

        {facets && facets.backends.length > 1 && (
          <select
            className={SELECT_CLASS}
            value={filters.backend ?? ''}
            onChange={(e) => set('backend', e.target.value || null)}
          >
            <option value="">All backends</option>
            {facets.backends.map((b) => (
              <option key={b} value={b}>
                {b}
              </option>
            ))}
          </select>
        )}

        {facets && facets.models.length > 1 && (
          <select
            className={SELECT_CLASS}
            value={filters.model ?? ''}
            onChange={(e) => set('model', e.target.value || null)}
          >
            <option value="">All models</option>
            {facets.models.map((m) => (
              <option key={m} value={m}>
                {m}
              </option>
            ))}
          </select>
        )}

        {facets && facets.formats.length > 1 && (
          <select
            className={SELECT_CLASS}
            value={filters.format ?? ''}
            onChange={(e) => set('format', e.target.value || null)}
          >
            <option value="">All formats</option>
            {facets.formats.map((f) => (
              <option key={f} value={f}>
                {f}
              </option>
            ))}
          </select>
        )}

        <Tabs value={filters.status} onValueChange={(v) => set('status', v as RequestFilters['status'])}>
          <TabsList>
            <TabsTrigger value="all">All</TabsTrigger>
            <TabsTrigger value="ok">Ok</TabsTrigger>
            <TabsTrigger value="error">Error</TabsTrigger>
          </TabsList>
        </Tabs>

        <button
          type="button"
          onClick={() => set('warnedOnly', !filters.warnedOnly)}
          className={cn(
            'h-7 rounded-control border px-2 text-xs transition-colors',
            filters.warnedOnly
              ? 'border-transparent bg-[color-mix(in_srgb,var(--color-warn)_14%,transparent)] text-warn'
              : 'border-border text-text-secondary hover:bg-surface-2',
          )}
        >
          Warnings only
        </button>

        {active && (
          <Button variant="ghost" onClick={() => onFiltersChange(EMPTY_FILTERS)}>
            Clear filters
          </Button>
        )}
      </div>

      {facets && facets.tags.length > 0 && (
        <div className="flex flex-wrap items-center gap-1.5">
          <span className="text-xs text-text-muted">Tags:</span>
          {facets.tags.map((t) => {
            const selected = filters.tag === t
            return (
              <button
                key={t}
                type="button"
                onClick={() => set('tag', selected ? null : t)}
                className={cn(
                  'rounded-full px-2.5 py-1 text-xs font-medium leading-none transition-opacity',
                  selected ? 'bg-accent text-accent-fg' : cn(tagChipClass(t), 'hover:opacity-80'),
                )}
              >
                {t}
              </button>
            )
          })}
        </div>
      )}

      {active && <ActiveFilterChips filters={filters} onFiltersChange={onFiltersChange} />}
    </div>
  )
}

function ActiveFilterChips({
  filters,
  onFiltersChange,
}: {
  filters: RequestFilters
  onFiltersChange: (next: RequestFilters) => void
}) {
  const chips: { label: string; clear: RequestFilters }[] = []
  if (filters.q) chips.push({ label: `search: ${filters.q}`, clear: { ...filters, q: '' } })
  if (filters.backend) chips.push({ label: `backend: ${filters.backend}`, clear: { ...filters, backend: null } })
  if (filters.model) chips.push({ label: `model: ${filters.model}`, clear: { ...filters, model: null } })
  if (filters.format) chips.push({ label: `format: ${filters.format}`, clear: { ...filters, format: null } })
  if (filters.tag) chips.push({ label: `tag: ${filters.tag}`, clear: { ...filters, tag: null } })
  if (filters.status !== 'all') chips.push({ label: `status: ${filters.status}`, clear: { ...filters, status: 'all' } })
  if (filters.warnedOnly) chips.push({ label: 'warnings only', clear: { ...filters, warnedOnly: false } })

  if (chips.length === 0) return null

  return (
    <div className="flex flex-wrap items-center gap-1.5">
      {chips.map((chip) => (
        <button key={chip.label} type="button" onClick={() => onFiltersChange(chip.clear)}>
          <Badge variant="neutral" className="gap-1">
            {chip.label}
            <span className="text-text-muted">×</span>
          </Badge>
        </button>
      ))}
    </div>
  )
}
