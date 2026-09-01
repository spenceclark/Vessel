import type { AggregateDimensionName, RequestFilters, SeriesGroupByName, SeriesMetricName, SessionScope } from './types'

/**
 * The list query's key, in one place. `useLiveHistory` has to match, invalidate and write
 * into exactly the key `RequestList` reads, and R10 was partly a symptom of that
 * relationship being implicit — two call sites building the same array by hand.
 */
export const REQUESTS_QUERY_ROOT = ['requests'] as const

export function requestsQueryKey(scope: SessionScope | null, filters: RequestFilters) {
  return [...REQUESTS_QUERY_ROOT, scope, filters] as const
}

/** DetailPane's per-request query key root — `['request', id]`. Shared so a clear (R14a) can find and evict every cached detail without hand-building the prefix. */
export const REQUEST_DETAIL_QUERY_ROOT = ['request'] as const

export function requestDetailQueryKey(id: number) {
  return [...REQUEST_DETAIL_QUERY_ROOT, id] as const
}

/**
 * Phase 7 D14 — the chart queries, keyed beside the existing roots. Every site that
 * invalidates `['stats']` must invalidate these roots too (they are aggregates over the
 * same rows): the deletion/clear paths in App and DataPanel and useLiveHistory's
 * authoritative refetch.
 */
export const SERIES_QUERY_ROOT = ['series'] as const

export function seriesQueryKey(
  metric: SeriesMetricName,
  groupBy: SeriesGroupByName,
  scope: SessionScope | null,
  filters: RequestFilters,
) {
  return [...SERIES_QUERY_ROOT, metric, groupBy, scope, filters] as const
}

export const AGGREGATE_QUERY_ROOT = ['aggregate'] as const

export function aggregateQueryKey(
  by: AggregateDimensionName,
  scope: SessionScope | null,
  filters: RequestFilters,
) {
  return [...AGGREGATE_QUERY_ROOT, by, scope, filters] as const
}
