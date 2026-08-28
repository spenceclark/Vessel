import { useEffect, useMemo, useRef } from 'react'
import { useInfiniteQuery, useQueryClient } from '@tanstack/react-query'
import { useVirtualizer } from '@tanstack/react-virtual'
import { api } from '@/api/client'
import type { RequestFilters, SessionScope, Summary } from '@/api/types'
import type { InFlightRequest } from '@/api/useEvents'
import type { Selection } from '@/App'
import { InFlightRow, RequestRow } from '@/components/RequestRow'
import { Mark } from '@/components/ui/Mark'

const PAGE_LIMIT = 100

/**
 * D6/D3 — reverse-chron virtualized history. In-flight rows (from SSE `started`, not yet
 * `completed`) pin above the loaded pages with a live timer. A `completed` event with a
 * row inserts it straight into the first page's cache entry rather than triggering a
 * refetch — cheap, and it can never race a REST page's own cursor (D5: duplicates
 * resolved in favor of REST rows, checked by id before inserting) — but only while no
 * filter beyond session scope is active; a new row may not match the active filter, and
 * refetching on every completion would defeat the cache, so instead a "new requests"
 * pill appears for the user to refresh on demand.
 *
 * The SSE subscription itself lives in App (ui-spec.md §9.1): selection spans both rows
 * and in-flight entries, and the in-flight detail pane needs the same `inFlight` map this
 * list renders from, so both are owned one level up and threaded down as props.
 */
export function RequestList({
  scope,
  filters,
  inFlight,
  now,
  newSinceFilter,
  onClearNewSinceFilter,
  selection,
  onSelectRow,
  onSelectInFlight,
}: {
  scope: SessionScope | null
  filters: RequestFilters
  inFlight: Map<number, InFlightRequest>
  now: number
  newSinceFilter: number
  onClearNewSinceFilter: () => void
  selection: Selection | null
  onSelectRow: (id: number) => void
  onSelectInFlight: (seq: number) => void
}) {
  const queryClient = useQueryClient()
  const parentRef = useRef<HTMLDivElement>(null)

  const queryKey = ['requests', scope, filters] as const

  const query = useInfiniteQuery({
    queryKey,
    queryFn: ({ pageParam }) =>
      api.listRequests({ limit: PAGE_LIMIT, before: pageParam, session: scope ?? undefined, filters }),
    initialPageParam: undefined as number | undefined,
    getNextPageParam: (lastPage) => lastPage.nextBefore ?? undefined,
    enabled: scope !== null,
  })

  const rows = useMemo(() => query.data?.pages.flatMap((p) => p.rows) ?? [], [query.data])
  const inFlightList = useMemo(() => Array.from(inFlight.values()), [inFlight])
  const itemCount = inFlightList.length + rows.length

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

  useEffect(() => {
    if (lastIndex === undefined) return
    if (lastIndex >= itemCount - 5 && query.hasNextPage && !query.isFetchingNextPage) {
      void query.fetchNextPage()
    }
  }, [lastIndex, itemCount, query.hasNextPage, query.isFetchingNextPage, query.fetchNextPage])

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
      {itemCount === 0 && !query.isLoading && (
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
