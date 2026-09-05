import { useQuery } from '@tanstack/react-query'
import { api } from '@/api/client'
import { aggregateQueryKey } from '@/api/queryKeys'
import type { AggregateResponse, AggregateDimensionName, AggregateRankName, RequestFilters, SessionScope } from '@/api/types'
import { AggregateBarCard } from '@/components/reports/AggregateBarCard'
import { ContextGrowthCard } from '@/components/reports/ContextGrowthCard'
import { ReportScopeBar } from '@/components/reports/ReportScopeBar'

/**
 * Phase 7 D10 — the Reports view: one full-width, internally-scrolling panel replacing the
 * list+detail row; the header (session picker, stat strip, toggle) stays shared. Carries
 * the History filters (S3) and renders them as clearable chips because the FilterBar is
 * not on screen here. Every query polls at the same 5s cadence as the header stats and is
 * enabled only while this view is open (D14).
 *
 * #26 live-use feedback added four cards without a new query in three cases: `Requests by
 * tag` and `Duration by tag` are projections of the *existing* by-tag fetch (percentiles
 * ride every `AggregateRow` already), and `Cache efficiency` is a projection of the
 * existing by-model fetch — only `Warnings by type` needed a genuinely new dimension
 * (`by=warning`). `hasTags`/`hasCacheData` are derived from those same fetches: the
 * context-growth card's Tag default and the cache card's hide-when-empty both come free.
 *
 * #26 live-use feedback (round 3) — a dimension with exactly one group has nothing to
 * compare, and for `Tokens`/`Requests`/`Avg tok/s` that single group's numbers are already
 * on screen verbatim in the header stats bar (Requests, Failed, Tokens In/Out, Avg tok/s):
 * a degenerate version of those cards would only restate the header. Those five card
 * instances (by-model and by-tag Tokens/Requests, and Avg tok/s by model) aren't mounted
 * at all once their fetch resolves to one group. `Duration by tag` (p50/p95), `Cache
 * efficiency` (the cached-% ratio) and `Warnings by type` (the code breakdown) all surface
 * something the header doesn't, so they stay — `AggregateBarCard` renders those as a
 * `StatPanel` instead of a one-bar chart.
 */
function useAggregate(
  by: AggregateDimensionName,
  scope: SessionScope,
  filters: RequestFilters,
  enabled: boolean,
  rank: AggregateRankName = 'tokens',
) {
  return useQuery<AggregateResponse>({
    queryKey: aggregateQueryKey(by, scope, filters, rank),
    queryFn: () => api.getAggregate({ by, session: scope, filters, rank }),
    enabled,
    refetchInterval: 5_000,
  })
}

export function ReportsView({
  scope,
  filters,
  onFiltersChange,
  sessionLabel,
  enabled,
  onSelectRequest,
}: {
  scope: SessionScope
  filters: RequestFilters
  onFiltersChange: (next: RequestFilters) => void
  sessionLabel: string
  enabled: boolean
  /** D13 — click a context-growth point: switch to history with that request selected. */
  onSelectRequest: (id: number) => void
}) {
  const byModel = useAggregate('model', scope, filters, enabled)
  const byTag = useAggregate('tag', scope, filters, enabled)
  const byWarning = useAggregate('warning', scope, filters, enabled)
  // #49 — the leaderboards. Both are score-*ranked* fetches, not projections of the
  // token-ranked ones: the server caps at 50 groups, so ranking by score after that cap
  // would drop a quiet 5/5 model behind fifty chatty 1/5 ones. by=patch is a new dimension
  // (one row per replay patch); its card is mounted only once the fetch proves the scope has
  // scored parameter sets in it.
  const byModelScore = useAggregate('model', scope, filters, enabled, 'score')
  const byPatch = useAggregate('patch', scope, filters, enabled, 'score')

  // #25 round 1 — undefined while byTag is still loading, so ContextGrowthCard's Tag
  // default doesn't flash on and off before the first fetch resolves.
  const hasTags = byTag.data ? byTag.data.rows.some((row) => row.key !== null) : undefined
  // #26 — "hides itself when the scope has no cache data ... rather than rendering
  // empty": default false (hidden) until a fetch actually proves otherwise.
  const hasCacheData = byModel.data ? byModel.data.rows.some((row) => row.tokensCachedRead > 0) : false
  // #26 round 3 — drop Tokens/Requests/Avg-tok/s by model|tag entirely once their own
  // fetch proves there's only one group to show (they'd only restate the header stats
  // bar); default to showing them while the fetch is still loading, not hiding-then-
  // popping-in once real data settles the question.
  // #49 — no parameter sets in scope means no per-param leaderboard to show at all (unlike
  // the by-model card, which states its own "score something" empty case).
  const hasPatchRows = byPatch.data ? byPatch.data.rows.length > 0 : false
  const showByModelBreakdown = byModel.data === undefined || byModel.data.totalGroups > 1
  const showByTagBreakdown = byTag.data === undefined || byTag.data.totalGroups > 1

  return (
    <div className="flex min-h-0 w-full flex-1 overflow-hidden rounded-panel border border-border bg-surface shadow-panel">
      <div className="flex w-full flex-col gap-3 overflow-y-auto p-3">
        <ReportScopeBar sessionLabel={sessionLabel} filters={filters} onFiltersChange={onFiltersChange} />
        <ContextGrowthCard
          scope={scope}
          filters={filters}
          enabled={enabled}
          hasTags={hasTags}
          onSelectRequest={onSelectRequest}
        />
        {/* §5 collapse rule — two columns from ~1100px, one below. */}
        <div className="grid grid-cols-1 gap-3 min-[1100px]:grid-cols-2">
          {/* #49 review — the card answering "which model should I switch to" leads the
              grid, unconditionally: its empty state is the discoverability hint for a
              feature nobody would otherwise find, so it has to be visible unscored too. */}
          <AggregateBarCard
            title="Score by model"
            data={byModelScore.data}
            by="model"
            projection="score"
            loading={byModelScore.isLoading}
          />
          {hasPatchRows && (
            <AggregateBarCard
              title="By parameter set"
              data={byPatch.data}
              by="patch"
              projection="score"
              loading={byPatch.isLoading}
            />
          )}
          {showByModelBreakdown && (
            <AggregateBarCard
              title="Tokens by model"
              data={byModel.data}
              by="model"
              projection="tokens"
              loading={byModel.isLoading}
            />
          )}
          {showByTagBreakdown && (
            <AggregateBarCard
              title="Tokens by tag"
              data={byTag.data}
              by="tag"
              projection="tokens"
              loading={byTag.isLoading}
            />
          )}
          {showByModelBreakdown && (
            <AggregateBarCard
              title="Requests by model"
              data={byModel.data}
              by="model"
              projection="requests"
              loading={byModel.isLoading}
            />
          )}
          {showByTagBreakdown && (
            <AggregateBarCard
              title="Requests by tag"
              data={byTag.data}
              by="tag"
              projection="requests"
              loading={byTag.isLoading}
            />
          )}
          {showByModelBreakdown && (
            <AggregateBarCard
              title="Avg tok/s by model"
              data={byModel.data}
              by="model"
              projection="rate"
              loading={byModel.isLoading}
            />
          )}
          <AggregateBarCard
            title="Duration by tag"
            data={byTag.data}
            by="tag"
            projection="duration"
            loading={byTag.isLoading}
          />
          {hasCacheData && (
            <AggregateBarCard
              title="Cache efficiency"
              data={byModel.data}
              by="model"
              projection="cache"
              loading={byModel.isLoading}
            />
          )}
          <AggregateBarCard
            title="Warnings by type"
            data={byWarning.data}
            by="warning"
            projection="requests"
            loading={byWarning.isLoading}
          />
        </div>
      </div>
    </div>
  )
}
