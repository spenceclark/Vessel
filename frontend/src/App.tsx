import { useCallback, useEffect, useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from '@/api/client'
import { EMPTY_FILTERS, type RequestFilters, type SessionScope } from '@/api/types'
import { StatsBar } from '@/components/StatsBar'
import { FilterBar } from '@/components/FilterBar'
import { RequestList } from '@/components/RequestList'
import { DetailPane } from '@/components/DetailPane'

/** D6/D3 — one screen: StatsBar / FilterBar / RequestList / DetailPane. No router. */
export default function App() {
  const queryClient = useQueryClient()
  const [scope, setScope] = useState<SessionScope | null>(null)
  const [currentSessionId, setCurrentSessionId] = useState<number | null>(null)
  const [selectedId, setSelectedId] = useState<number | null>(null)
  const [filters, setFilters] = useState<RequestFilters>(EMPTY_FILTERS)

  const sessionsQuery = useQuery({ queryKey: ['sessions'], queryFn: api.listSessions })

  // Default view = current session (D6): the newest session marker, once it's known.
  useEffect(() => {
    if (currentSessionId !== null) return
    const newest = sessionsQuery.data?.[0]
    if (newest) {
      setCurrentSessionId(newest.id)
      setScope(newest.id)
    }
  }, [sessionsQuery.data, currentSessionId])

  const handleReset = useCallback(async () => {
    const session = await api.createSession()
    setCurrentSessionId(session.id)
    setScope(session.id)
    setSelectedId(null)
    await queryClient.invalidateQueries({ queryKey: ['sessions'] })
  }, [queryClient])

  return (
    <div className="flex h-screen flex-col">
      <StatsBar
        scope={scope}
        currentSessionId={currentSessionId}
        onScopeChange={setScope}
        onReset={handleReset}
      />
      <div className="flex min-h-0 flex-1">
        <div className="flex w-[420px] shrink-0 flex-col border-r border-[var(--border)]">
          <FilterBar scope={scope} filters={filters} onFiltersChange={setFilters} />
          <div className="min-h-0 flex-1">
            <RequestList scope={scope} filters={filters} selectedId={selectedId} onSelect={setSelectedId} />
          </div>
        </div>
        <div className="min-w-0 flex-1">
          <DetailPane id={selectedId} />
        </div>
      </div>
    </div>
  )
}
