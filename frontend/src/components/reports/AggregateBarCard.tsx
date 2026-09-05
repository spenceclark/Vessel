import { MAX_SCORE, type AggregateDimensionName, type AggregateResponse, type AggregateRow } from '@/api/types'
import { CHART_RAMP } from '@/lib/chartColors'
import { formatCompactTokenCount, formatMs, formatTokPerSec } from '@/lib/format'
import { cn } from '@/lib/utils'
import { BarChart } from '@/components/ui/chart/BarChart'
import { ChartLegend } from '@/components/ui/chart/ChartLegend'

/** D12 — what a card projects out of one aggregate fetch. */
export type AggregateProjection = 'tokens' | 'requests' | 'rate' | 'cache' | 'duration' | 'score'

/**
 * A 180px card only reads cleanly with a handful of bars — independent of the API's own
 * `ChartLimits.MaxGroups` (50, a query-cost bound, not a display one). The card's own
 * cap note and aria-label disclose *this* count against `totalGroups`, never the larger
 * API row count, so "top 8 of 50" doesn't silently mean "top 50 of 137" underneath.
 */
const DISPLAY_ROWS = 8

/** One tile in the degenerate-scope stat panel (see `statFields` below). */
interface StatField {
  label: string
  value: string
  danger?: boolean
}

interface Projection {
  measures: { name: string; colorVar: string }[]
  mode: 'grouped' | 'stacked'
  values: (rows: AggregateResponse['rows']) => (number | null)[][]
  formatValue: (rows: AggregateResponse['rows']) => (v: number) => string
  legend: boolean
  /** #49 — a fixed axis maximum, for a measure whose scale is meaningful (a 1-5 score). */
  domainMax?: number
  /** #49 — a per-bar suffix beyond the value itself (mean · n · win rate). */
  label?: (row: AggregateRow) => string
  /**
   * #26 live-use feedback (round 2) — a single-group scope's stat panel: the group's own
   * numbers, laid out as tiles (never plain prose — see `AggregateBarCard`'s degenerate
   * branch), not counting the group's own key/name (rendered once, above the tiles).
   */
  statFields: (row: AggregateRow, formatValue: (v: number) => string) => StatField[]
}

// D2 — every aggregate query is sorted by tokens in+out desc (then requests, then key);
// that ordering is what "top N" ranks by, regardless of which measure a given card plots.
const RANKED_BY = 'tokens'

// #26 live-use feedback — the fan-out disclosure, worded per dimension (tag vs warning).
const FAN_OUT_NOTE: Partial<Record<AggregateDimensionName, string>> = {
  tag: 'A request with several tags is counted once per tag.',
  warning: 'A request with several warnings is counted once per warning.',
}

const PROJECTIONS: Record<AggregateProjection, Projection> = {
  tokens: {
    measures: [
      { name: 'tokens in', colorVar: CHART_RAMP[0]! },
      { name: 'tokens out', colorVar: CHART_RAMP[1]! },
    ],
    mode: 'grouped',
    values: (rows) => rows.map((row) => [row.tokensIn, row.tokensOut]),
    formatValue: (rows) => {
      const estimated = rows.some((row) => row.tokensEstimated)
      return (v) => formatCompactTokenCount(v, estimated)
    },
    legend: true,
    statFields: (row, fmt) => [
      { label: 'Tokens in', value: fmt(row.tokensIn) },
      { label: 'Tokens out', value: fmt(row.tokensOut) },
    ],
  },
  requests: {
    measures: [
      { name: 'ok', colorVar: CHART_RAMP[0]! },
      // --danger appears only for a genuinely failed quantity (§2.3).
      { name: 'failed', colorVar: 'var(--color-danger)' },
    ],
    mode: 'stacked',
    values: (rows) => rows.map((row) => [row.requests - row.failed, row.failed]),
    formatValue: () => (v) => v.toLocaleString('en-US'),
    legend: true,
    statFields: (row) => [
      { label: 'Requests', value: row.requests.toLocaleString('en-US') },
      ...(row.failed > 0 ? [{ label: 'Failed', value: row.failed.toLocaleString('en-US'), danger: true }] : []),
    ],
  },
  rate: {
    measures: [{ name: 'avg tok/s', colorVar: CHART_RAMP[0]! }],
    mode: 'grouped',
    // D12/D13 — "—" for groups with no measured rate, never 0: null draws no bar and the
    // sr-only table shows an em-dash.
    values: (rows) => rows.map((row) => [row.avgTokPerSec]),
    // §8.6 — routes through lib/format.ts like every other projection (duration's formatMs
    // does the same, baking "ms"/"s" into the axis/legend/stat value alike).
    formatValue: () => formatTokPerSec,
    legend: false,
    statFields: (row) => [{ label: 'Avg tok/s', value: formatTokPerSec(row.avgTokPerSec) }],
  },
  // #26 live-use feedback — "the money chart for live-API users (prompt-cache misses are
  // silent cost)". Reuses whatever `by=` query the card is given (tokensIn/tokensCachedRead
  // are already in every AggregateRow) — no new endpoint. ReportsView hides this card
  // entirely when no row in scope has any cached tokens, rather than rendering it empty.
  cache: {
    measures: [
      { name: 'cached', colorVar: CHART_RAMP[0]! },
      { name: 'uncached', colorVar: CHART_RAMP[5]! },
    ],
    mode: 'stacked',
    values: (rows) => rows.map((row) => [row.tokensCachedRead, Math.max(0, row.tokensIn - row.tokensCachedRead)]),
    formatValue: (rows) => {
      const estimated = rows.some((row) => row.tokensEstimated)
      return (v) => formatCompactTokenCount(v, estimated)
    },
    legend: true,
    statFields: (row, fmt) => {
      const pct = row.tokensIn > 0 ? Math.round((100 * row.tokensCachedRead) / row.tokensIn) : 0
      return [
        { label: 'Cached', value: `${pct}%` },
        { label: 'Cached tokens', value: fmt(row.tokensCachedRead) },
        { label: 'Tokens in', value: fmt(row.tokensIn) },
      ]
    },
  },
  // #49 — the leaderboard: mean human score on a *fixed* 0-5 axis, because auto-scaling
  // 3.9 against 4.1 to fill the card is a lie about how different they are. Unscored groups
  // are dropped rather than drawn as zero (see `scoreRows` below).
  score: {
    measures: [{ name: 'mean score', colorVar: CHART_RAMP[0]! }],
    mode: 'grouped',
    values: (rows) => rows.map((row) => [row.meanScore]),
    formatValue: () => (v) => v.toFixed(1),
    legend: false,
    domainMax: MAX_SCORE,
    label: (row) => {
      const mean = row.meanScore == null ? '—' : row.meanScore.toFixed(1)
      const wins = row.wins != null && row.groups ? ` · top ${row.wins}/${row.groups}` : ''
      return `${mean} · n=${row.scored}${wins}`
    },
    statFields: (row) => [
      { label: 'Mean score', value: row.meanScore == null ? '—' : `${row.meanScore.toFixed(1)}/${MAX_SCORE}` },
      { label: 'Scored', value: row.scored.toLocaleString('en-US') },
      ...(row.wins != null && row.groups ? [{ label: 'Fan wins', value: `${row.wins}/${row.groups}` }] : []),
    ],
  },
  // #26 live-use feedback — "averages hide the tail latencies that make an agent feel
  // slow"; nearest-rank p50/p95 computed server-side (Summary.cs `AggregateRow`), never
  // client-derived from an average.
  duration: {
    measures: [
      { name: 'p50', colorVar: CHART_RAMP[0]! },
      { name: 'p95', colorVar: CHART_RAMP[3]! },
    ],
    mode: 'grouped',
    values: (rows) => rows.map((row) => [row.p50DurationMs, row.p95DurationMs]),
    formatValue: () => (v) => formatMs(v),
    legend: true,
    statFields: (row) => [
      { label: 'p50', value: formatMs(row.p50DurationMs) },
      { label: 'p95', value: formatMs(row.p95DurationMs) },
    ],
  },
}

/**
 * The degenerate-scope stat panel (#26 live-use feedback, round 3 — "can it look nicer
 * than plain text"): the group's own name once, then its numbers as `<dl>` tiles —
 * `--surface-3` (one level deeper than this card's own `--surface-2`, per the depth
 * model), xs uppercase label over a mono stat value, mirroring the header stat bar's own
 * `Stat` look (StatsBar.tsx) at a size that doesn't compete with it — `text-stat` is
 * reserved for the header alone (§3 Typography).
 */
function StatPanel({ groupKey, fields }: { groupKey: string | null; fields: StatField[] }) {
  return (
    <div className="mt-2">
      <div className="text-xs font-medium text-text-secondary">{groupKey ?? '(none)'}</div>
      <dl className="mt-1.5 flex flex-wrap gap-2">
        {fields.map((field) => (
          <div key={field.label} className="rounded-control bg-surface-3 px-2.5 py-1.5">
            <dt className="text-xs font-[550] uppercase tracking-[0.06em] text-text-muted">{field.label}</dt>
            <dd className={cn('mt-0.5 font-mono text-base font-semibold tabular-nums', field.danger ? 'text-danger' : 'text-text')}>
              {field.value}
            </dd>
          </div>
        ))}
      </dl>
    </div>
  )
}

/**
 * Phase 7 D12 (+ #26 live-use feedback) — the aggregate-fetch report cards. Same surface-2
 * block and xs uppercase label as §5.2's metric cards; horizontal bars per §2.3's form
 * rules. A scope where this dimension has exactly one group renders as a `StatPanel`
 * instead of a one-bar chart — a bar chart with one bar carries no comparison, only the
 * panel's own tiles do. (Cards whose degenerate tiles would only restate a header stat —
 * Tokens/Requests by model/tag, Avg tok/s by model — aren't mounted at all in that case;
 * `ReportsView` makes that call before ever rendering this component.)
 */
export function AggregateBarCard({
  title,
  data,
  by,
  projection,
  loading,
}: {
  title: string
  data: AggregateResponse | undefined
  by: AggregateDimensionName
  projection: AggregateProjection
  loading: boolean
}) {
  const spec = PROJECTIONS[projection]
  // #49 — a leaderboard ranks by the score, and a group nobody scored is not a last place;
  // it is absent. Every other projection keeps the API's own tokens-desc order.
  const ranked = data
    ? projection === 'score'
      ? data.rows.filter((row) => row.scored > 0).sort((a, b) => (b.meanScore ?? 0) - (a.meanScore ?? 0))
      : data.rows
    : []
  const displayRows = ranked.slice(0, DISPLAY_ROWS)
  const hasRows = displayRows.length > 0
  const capNote = data && displayRows.length < ranked.length
    ? `Top ${displayRows.length} of ${ranked.length.toLocaleString('en-US')} by ${projection === 'score' ? 'mean score' : RANKED_BY}.`
    : null
  const fanOutNote = FAN_OUT_NOTE[by]

  return (
    <section className="rounded-control bg-surface-2 p-3">
      <h3 className="text-xs font-[550] uppercase tracking-[0.06em] text-text-muted">{title}</h3>
      {data === undefined ? (
        <div className="flex h-[180px] items-center justify-center text-sm text-text-muted" aria-live="polite">
          {loading ? '…' : '—'}
        </div>
      ) : projection === 'score' && displayRows.length === 0 ? (
        <p className="flex h-[180px] items-center justify-center text-sm text-text-muted">
          Score some compares to see a leaderboard.
        </p>
      ) : ranked.length === 1 ? (
        <StatPanel groupKey={ranked[0]!.key} fields={spec.statFields(ranked[0]!, spec.formatValue(displayRows))} />
      ) : (
        <>
          <BarChart
            rows={displayRows.map((row) => ({ key: row.key, note: spec.label?.(row) }))}
            domainMax={spec.domainMax}
            values={spec.values(displayRows)}
            measures={spec.measures}
            mode={spec.mode}
            height={180}
            label={hasRows ? `${title} by ${by}, top ${displayRows.length} of ${data.totalGroups} groups.` : `${title} — no data in this scope.`}
            formatValue={spec.formatValue(displayRows)}
            emptyText={loading ? 'Loading…' : 'No requests in this scope.'}
          />
          {spec.legend && (
            <div className="mt-2">
              <ChartLegend entries={spec.measures.map((measure) => ({ label: measure.name, colorVar: measure.colorVar }))} />
            </div>
          )}
          {(capNote || fanOutNote) && (
            <p className="mt-1 text-xs text-text-muted">
              {capNote}
              {capNote && fanOutNote ? ' ' : ''}
              {fanOutNote}
            </p>
          )}
          {(projection === 'rate' || projection === 'duration') && (
            <p className="mt-1 text-xs text-text-muted">
              — = no measured {projection === 'rate' ? 'rate' : 'duration'} for the group.
            </p>
          )}
        </>
      )}
    </section>
  )
}
