import { useEffect, useMemo, useRef } from 'react'
import { useInfiniteQuery, useQueryClient } from '@tanstack/react-query'
import { useVirtualizer } from '@tanstack/react-virtual'
import { api } from '@/api/client'
import { filtersActive, type RequestFilters, type SessionScope, type Summary } from '@/api/types'
import { requestsQueryKey } from '@/api/queryKeys'
import { useNowTick, type InFlightRequest } from '@/api/useEvents'
import type { Selection } from '@/App'
import { InFlightRow, RequestRow } from '@/components/RequestRow'
import { ErrorState } from '@/components/ui/ErrorState'
import { Mark } from '@/components/ui/Mark'

const PAGE_LIMIT = 100

/**
 * D6/D3 — reverse-chron virtualized history. In-flight rows (from SSE `started`, not yet
 * `completed`) pin above the loaded pages with a live timer.
 *
 * This component only *renders*: the SSE subscription, the in-flight map, cache merging
 * and reconciliation all live in `useLiveHistory` (R10/R11/D05), and reach here as props.
 * Keeping them out of the list matters because the detail pane needs the same live state,
 * and because a completion must be merged correctly whether or not this list is mounted or
 * mid-fetch.
 *
 * A `completed` row is spliced into the first page's cache entry rather than triggering a
 * refetch — cheap, and it can never race a REST page's own cursor (D5: duplicates resolved
 * in favor of REST rows, checked by id) — but only while no filter beyond session scope is
 * active; a new row may not match the active filter, so instead a "new requests" pill
 * appears for the user to refresh on demand.
 */
export function RequestList({
  scope,
  filters,
  inFlight,
  newSinceFilter,
  onClearNewSinceFilter,
  selection,
  onSelectRow,
  onSelectInFlight,
}: {
  scope: SessionScope | null
  filters: RequestFilters
  inFlight: InFlightRequest[]
  newSinceFilter: number
  onClearNewSinceFilter: () => void
  selection: Selection | null
  onSelectRow: (id: number) => void
  onSelectInFlight: (seq: number) => void
}) {
  const queryClient = useQueryClient()
  const parentRef = useRef<HTMLDivElement>(null)

  const queryKey = requestsQueryKey(scope, filters)

  const query = useInfiniteQuery({
    queryKey,
    // K0a — `signal` is TanStack's per-fetch abort signal; passing it on is what makes
    // `cancelQueries` (recovery / clear) actually abandon an outstanding list read.
    queryFn: ({ pageParam, signal }) =>
      api.listRequests({ limit: PAGE_LIMIT, before: pageParam, session: scope ?? undefined, filters, signal }),
    initialPageParam: undefined as number | undefined,
    getNextPageParam: (lastPage) => lastPage.nextBefore ?? undefined,
    enabled: scope !== null,
  })

  const rows = useMemo(() => query.data?.pages.flatMap((p) => p.rows) ?? [], [query.data])

  // D05 — in-flight rows obey session scope (applied upstream in useLiveHistory) and
  // nothing else. With any other filter active they can't be filtered honestly: they have
  // no final status, model or warnings yet, so matching them against those predicates
  // would be guesswork. They collapse to a count instead of silently lying either way.
  const filtered = filtersActive(filters)
  const inFlightList = useMemo(() => (filtered ? [] : inFlight), [filtered, inFlight])
  const itemCount = inFlightList.length + rows.length

  // R04 (review §4 risk) — owned here, not lifted to App: the running in-flight timer
  // only needs to rerender this list (and, separately, the in-flight detail pane), never
  // App's siblings (StatsBar/FilterBar/DetailPane) that a shared top-level tick used to
  // drag along every 250ms regardless of what was actually showing. Also only ticks at
  // all while there's something in-flight to animate.
  const now = useNowTick(250, inFlightList.length > 0)

  // oxlint-disable-next-line react/incompatible-library -- TanStack Virtual owns DOM measurement and this list is intentionally not compiler-memoized.
  const virtualizer = useVirtualizer({
    count: itemCount,
    getScrollElement: () => parentRef.current,
    estimateSize: () => 52,
    overscan: 12,
    // Without a stable key, the virtualizer caches each measured row height by
    // index — when a live row is spliced in at the front, every row below shifts
    // index by one and inherits whatever height was cached there for a *different*
    // row until it happens to remeasure, rendering visually truncated in the
    // meantime. Keying by the same identity the render loop below uses (seq for
    // in-flight, id for loaded rows) ties each cached height to the actual row.
    getItemKey: (index) =>
      index < inFlightList.length ? `inflight-${inFlightList[index].seq}` : `row-${rows[index - inFlightList.length]?.id}`,
  })

  const virtualItems = virtualizer.getVirtualItems()
  const lastIndex = virtualItems.at(-1)?.index
  const { hasNextPage, isFetchingNextPage, fetchNextPage } = query

  useEffect(() => {
    if (lastIndex === undefined) return
    if (lastIndex >= itemCount - 5 && hasNextPage && !isFetchingNextPage) {
      void fetchNextPage()
    }
  }, [lastIndex, itemCount, hasNextPage, isFetchingNextPage, fetchNextPage])

  return (
    <div ref={parentRef} className="h-full overflow-y-auto">
      {newSinceFilter > 0 && (
        <button
          type="button"
          onClick={() => {
            onClearNewSinceFilter()
            void queryClient.invalidateQueries({ queryKey })
          }}
          className="sticky top-0 z-10 w-full border-b border-border bg-[color-mix(in_srgb,var(--color-accent)_10%,transparent)] px-3 py-1.5 text-center text-xs font-medium text-accent hover:bg-[color-mix(in_srgb,var(--color-accent)_15%,transparent)]"
        >
          {newSinceFilter} new request{newSinceFilter === 1 ? '' : 's'} — refresh
        </button>
      )}
      {filtered && inFlight.length > 0 && (
        <div className="sticky top-0 z-10 flex items-center gap-2 border-b border-border bg-[color-mix(in_srgb,var(--color-accent)_5%,transparent)] px-3 py-1.5 text-xs text-text-secondary">
          <span className="pulse-dot h-2 w-2 shrink-0 rounded-full bg-accent" aria-hidden="true" />
          {inFlight.length} in flight — not shown while a filter is active
        </div>
      )}
      {itemCount === 0 && query.isError && (
        <ErrorState message="Failed to load requests." onRetry={() => query.refetch()} />
      )}
      {itemCount === 0 && !query.isLoading && !query.isError && (
        <div className="flex flex-col items-center gap-2 p-8 text-center">
          <Mark size={28} muted />
          <p className="text-sm text-text-muted">No requests yet — traffic through Vessel will show up here.</p>
        </div>
      )}
      <div style={{ height: virtualizer.getTotalSize(), position: 'relative' }}>
        {virtualItems.map((virtualRow) => {
          const isInFlight = virtualRow.index < inFlightList.length
          const inFlightItem = isInFlight ? inFlightList[virtualRow.index] : undefined
          const row = isInFlight ? undefined : rows[virtualRow.index - inFlightList.length]

          return (
            <div
              key={isInFlight ? `inflight-${inFlightItem!.seq}` : `row-${row!.id}`}
              ref={virtualizer.measureElement}
              data-index={virtualRow.index}
              style={{
                position: 'absolute',
                top: 0,
                left: 0,
                width: '100%',
                transform: `translateY(${virtualRow.start}px)`,
              }}
            >
              {isInFlight ? (
                <InFlightRow
                  item={inFlightItem as InFlightRequest}
                  now={now}
                  selected={selection?.kind === 'inflight' && selection.seq === (inFlightItem as InFlightRequest).seq}
                  onSelect={onSelectInFlight}
                />
              ) : (
                <RequestRow
                  row={row as Summary}
                  selected={selection?.kind === 'row' && selection.id === row!.id}
                  onSelect={onSelectRow}
                />
              )}
            </div>
          )
        })}
      </div>
    </div>
  )
}
