import { useEffect, useRef, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { api } from '@/api/client'
import { seriesQueryKey } from '@/api/queryKeys'
import type { RequestFilters, SeriesGroupByName, SeriesResponse, SessionScope } from '@/api/types'
import { assignSeriesColors } from '@/lib/chartColors'
import { formatTokenCount } from '@/lib/format'
import { LineChart } from '@/components/ui/chart/LineChart'
import { ContextGrowthSmallMultiples } from '@/components/reports/ContextGrowthSmallMultiples'
import { Tabs, TabsList, TabsTrigger } from '@/components/ui/tabs'

const GROUP_BY_OPTIONS: { value: SeriesGroupByName; label: string }[] = [
  { value: 'none', label: 'None' },
  { value: 'tag', label: 'Tag' },
  { value: 'model', label: 'Model' },
]

const GROUP_BY_NOUN: Record<SeriesGroupByName, string> = {
  none: 'requests',
  tag: 'tags',
  model: 'models',
  backend: 'backends',
}

type ViewMode = 'overlay' | 'grid'

/**
 * Phase 7 D12 #25 — the context-growth card: tokens_in per request over the run, so
 * context bloat is visible at a glance. Group-by is this card's own control (None |
 * Tag | Model). The server caps points and series; both caps are disclosed here, never
 * silently applied (D1).
 *
 * #25 live-use feedback (round 1): a single ungrouped line through several interleaved
 * agents plots the interleaving, not growth — None renders as a scatter, never a
 * connected line, and defaults away from itself the moment the scope has any tagged
 * traffic (a manual pick of None is never overridden once made).
 *
 * #25 live-use feedback (round 2): grouped series can be "meaningful alone, meaningless
 * together" — a stateless one-shot agent's high-variance line buries a stateful agent's
 * smooth trend when both overlay. The Overlay/Grid toggle (shown once there's more than
 * one series to separate) switches to one mini-chart per series on a shared y-axis
 * (`ContextGrowthSmallMultiples`) instead. `viewMode` is reset back to 'overlay' whenever
 * that condition (`canGrid`) stops holding — switching groupBy to one with a single
 * series (e.g. Model in a single-model session), or a session/filter change that leaves
 * fewer than two series — not just on the None transition; `viewMode` is this card's own
 * state and outlives both of those changes, so a stale Grid selection would otherwise
 * keep rendering `ContextGrowthSmallMultiples` with one lone mini-chart instead of the
 * full-width overlay chart the toggle no longer even offers.
 */
export function ContextGrowthCard({
  scope,
  filters,
  enabled,
  hasTags,
  onSelectRequest,
}: {
  scope: SessionScope
  filters: RequestFilters
  enabled: boolean
  /** From the sibling by-tag aggregate fetch — undefined while that's still loading. */
  hasTags: boolean | undefined
  onSelectRequest: (id: number) => void
}) {
  const [groupBy, setGroupBy] = useState<SeriesGroupByName>('none')
  const [viewMode, setViewMode] = useState<ViewMode>('overlay')
  const userChangedGroupBy = useRef(false)

  // #25 round 1 — default to Tag once the scope is known to carry tagged traffic, but
  // never fight a groupBy the user picked themselves (including picking None back).
  useEffect(() => {
    if (hasTags && !userChangedGroupBy.current) setGroupBy('tag')
  }, [hasTags])

  function handleGroupByChange(next: SeriesGroupByName) {
    userChangedGroupBy.current = true
    setGroupBy(next)
  }

  const query = useQuery({
    queryKey: seriesQueryKey('tokens_in', groupBy, scope, filters),
    queryFn: () => api.getSeries({ metric: 'tokens_in', groupBy, session: scope, filters }),
    enabled,
    refetchInterval: 5_000,
  })
  const data: SeriesResponse | undefined = query.data

  const series = data?.series ?? []
  // Single source of truth for "is Overlay/Grid even a real choice right now" — both the
  // toggle's own visibility and the render path below key off this, so they can never
  // disagree (a mismatch there is exactly what produced the reported bug: the toggle
  // vanished but a stale Grid selection kept rendering small multiples anyway).
  const canGrid = groupBy !== 'none' && series.length > 1
  useEffect(() => {
    // oxlint-disable-next-line react/set-state-in-effect -- synchronizes viewMode with canGrid, which the render path already keys off directly; this only needs to correct the state itself so a later flip back to a multi-series scope doesn't inherit a stale 'grid'.
    if (!canGrid) setViewMode('overlay')
  }, [canGrid])
  const colors = assignSeriesColors(series.map((s) => s.key), groupBy === 'tag')
  const peak = Math.max(0, ...series.flatMap((s) => s.points.map((p) => p.v)))
  const estimated = data?.estimated ?? false
  const formatValue = (v: number) => formatTokenCount(v, estimated)
  const formatTime = (iso: string) => new Date(iso).toLocaleString()

  // D8 — one-sentence summary including the headline value. `data.returned` is always a
  // request count (the server's DENSE_RANK cap counts distinct requests, not fanned-out
  // rows), never a tag/model count — only the series count below borrows the group-by noun.
  const seriesNoun = series.length === 1 ? GROUP_BY_NOUN[groupBy].replace(/s$/, '') : GROUP_BY_NOUN[groupBy]
  const label =
    data && series.length > 0
      ? `Context growth: tokens in per request over time, ${data.returned.toLocaleString('en-US')} requests${
          groupBy === 'none' ? '' : `, ${series.length} ${seriesNoun}`
        }, peak ${peak.toLocaleString('en-US')}`
      : 'Context growth: tokens in per request over time — no data in this scope.'

  return (
    <section className="rounded-control bg-surface-2 p-3">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h3 className="text-xs font-[550] uppercase tracking-[0.06em] text-text-muted">Context growth</h3>
        <div className="flex items-center gap-2">
          {canGrid && (
            <Tabs value={viewMode} onValueChange={(v) => setViewMode(v as ViewMode)}>
              <TabsList aria-label="Chart layout">
                <TabsTrigger value="overlay">Overlay</TabsTrigger>
                <TabsTrigger value="grid">Grid</TabsTrigger>
              </TabsList>
            </Tabs>
          )}
          <Tabs value={groupBy} onValueChange={(v) => handleGroupByChange(v as SeriesGroupByName)}>
            <TabsList aria-label="Group context growth by">
              {GROUP_BY_OPTIONS.map((option) => (
                <TabsTrigger key={option.value} value={option.value}>
                  {option.label}
                </TabsTrigger>
              ))}
            </TabsList>
          </Tabs>
        </div>
      </div>

      <div className="mt-2">
        {viewMode === 'grid' && canGrid ? (
          <ContextGrowthSmallMultiples
            series={series}
            colors={colors}
            formatValue={formatValue}
            formatTime={formatTime}
            onSelectPoint={onSelectRequest}
          />
        ) : (
          <LineChart
            series={series}
            colors={colors}
            height={240}
            label={label}
            formatValue={formatValue}
            formatTime={formatTime}
            onSelectPoint={onSelectRequest}
            renderMode={groupBy === 'none' ? 'scatter' : 'line'}
            emptyText={query.isLoading ? 'Loading…' : 'No requests with token counts in this scope.'}
          />
        )}
      </div>

      {data && (data.truncated || groupBy === 'tag' || data.omittedSeries > 0) && (
        <p className="mt-1 text-xs text-text-muted">
          {data.truncated &&
            `Most recent ${data.returned.toLocaleString('en-US')} of ${data.totalMatching.toLocaleString('en-US')} requests.`}
          {data.truncated && groupBy === 'tag' && ' '}
          {groupBy === 'tag' && 'A request with several tags appears in each.'}
          {groupBy === 'tag' && data.omittedSeries > 0 && ' '}
          {data.omittedSeries > 0 &&
            `${data.omittedSeries} more ${data.omittedSeries === 1 ? 'series' : 'series'} not shown, ranked out by total tokens.`}
        </p>
      )}

      {estimated && <p className="mt-1 text-xs text-text-muted">~ Estimated token counts — totals are approximate.</p>}
    </section>
  )
}
