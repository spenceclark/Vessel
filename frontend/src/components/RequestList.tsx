import { useEffect, useMemo, useRef } from 'react'
import { useInfiniteQuery, useQueryClient, type InfiniteData } from '@tanstack/react-query'
import { useVirtualizer } from '@tanstack/react-virtual'
import { api } from '@/api/client'
import type { RequestListResponse, SessionScope, Summary } from '@/api/types'
import { useEvents, useNowTick, type InFlightRequest } from '@/api/useEvents'
import { InFlightRow, RequestRow } from '@/components/RequestRow'

const PAGE_LIMIT = 100

/**
 * D6 — reverse-chron virtualized history. In-flight rows (from SSE `started`, not yet
 * `completed`) pin above the loaded pages with a live timer. A `completed` event with a
 * row inserts it straight into the first page's cache entry rather than triggering a
 * refetch — cheap, and it can never race a REST page's own cursor (D5: duplicates
 * resolved in favor of REST rows, checked by id before inserting).
 */
export function RequestList({
  scope,
  selectedId,
  onSelect,
}: {
  scope: SessionScope | null
  selectedId: number | null
  onSelect: (id: number) => void
}) {
  const queryClient = useQueryClient()
  const parentRef = useRef<HTMLDivElement>(null)

  const queryKey = ['requests', scope] as const

  const query = useInfiniteQuery({
    queryKey,
    queryFn: ({ pageParam }) =>
      api.listRequests({ limit: PAGE_LIMIT, before: pageParam, session: scope ?? undefined }),
    initialPageParam: undefined as number | undefined,
    getNextPageParam: (lastPage) => lastPage.nextBefore ?? undefined,
    enabled: scope !== null,
  })

  const { inFlight } = useEvents((row) => {
    if (!row || scope === null) return
    if (scope !== 'all' && row.sessionId !== scope) return

    queryClient.setQueryData<InfiniteData<RequestListResponse, number | undefined>>(queryKey, (old) => {
      if (!old) return old
      const first = old.pages[0]
      if (first.rows.some((r) => r.id === row.id)) return old
      const pages = [...old.pages]
      pages[0] = { ...first, rows: [row, ...first.rows] }
      return { ...old, pages }
    })
  })

  const now = useNowTick(250)

  const rows = useMemo(() => query.data?.pages.flatMap((p) => p.rows) ?? [], [query.data])
  const inFlightList = useMemo(() => Array.from(inFlight.values()), [inFlight])
  const itemCount = inFlightList.length + rows.length

  const virtualizer = useVirtualizer({
    count: itemCount,
    getScrollElement: () => parentRef.current,
    estimateSize: () => 52,
    overscan: 12,
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
      {itemCount === 0 && !query.isLoading && (
        <div className="p-6 text-center text-sm text-[var(--muted)]">
          No requests yet — traffic through Vessel will show up here.
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
                <InFlightRow item={inFlightItem as InFlightRequest} now={now} />
              ) : (
                <RequestRow row={row as Summary} selected={row!.id === selectedId} onSelect={onSelect} />
              )}
            </div>
          )
        })}
      </div>
    </div>
  )
}
