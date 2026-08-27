import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { api } from '@/api/client'
import type { SessionScope } from '@/api/types'
import { Button } from '@/components/ui/button'
import { ConfirmDialog } from '@/components/ui/dialog'
import { cn } from '@/lib/utils'
import { formatMs, formatTokPerSec } from '@/lib/format'

/** D6 — session stats + Reset + backend health dots, scoped to whatever the list is viewing. */
export function StatsBar({
  scope,
  currentSessionId,
  onScopeChange,
  onReset,
}: {
  scope: SessionScope | null
  currentSessionId: number | null
  onScopeChange: (scope: SessionScope) => void
  onReset: () => Promise<void>
}) {
  const [confirmOpen, setConfirmOpen] = useState(false)
  const [resetting, setResetting] = useState(false)

  const statsQuery = useQuery({
    queryKey: ['stats', scope],
    queryFn: () => api.getStats(scope ?? undefined),
    enabled: scope !== null,
    refetchInterval: 5000,
  })

  const statusQuery = useQuery({
    queryKey: ['status'],
    queryFn: api.getStatus,
    staleTime: 60_000,
  })

  const stats = statsQuery.data

  async function handleConfirmReset() {
    setConfirmOpen(false)
    setResetting(true)
    try {
      await onReset()
    } finally {
      setResetting(false)
    }
  }

  return (
    <div className="flex flex-wrap items-center gap-x-6 gap-y-2 border-b border-[var(--border)] px-4 py-2">
      <div className="flex items-center gap-4 text-sm">
        <Stat label="Requests" value={stats ? String(stats.total) : '—'} />
        <Stat
          label="Failed"
          value={stats ? String(stats.failed) : '—'}
          danger={!!stats && stats.failed > 0}
        />
        <Stat label="Avg latency" value={formatMs(stats?.avgDurationMs)} />
        <Stat label="Avg tok/s" value={formatTokPerSec(stats?.avgTokPerSec)} />
        <Stat label="Avg TTFT" value={formatMs(stats?.avgTtftMs)} />
      </div>

      <div className="ml-auto flex items-center gap-2">
        <div className="flex items-center rounded-md border border-[var(--border)] p-0.5 text-xs">
          <button
            type="button"
            disabled={currentSessionId === null}
            onClick={() => currentSessionId !== null && onScopeChange(currentSessionId)}
            className={cn(
              'rounded px-2 py-1',
              scope !== 'all' ? 'bg-[var(--card)] font-medium' : 'text-[var(--muted)]',
            )}
          >
            Current
          </button>
          <button
            type="button"
            onClick={() => onScopeChange('all')}
            className={cn('rounded px-2 py-1', scope === 'all' ? 'bg-[var(--card)] font-medium' : 'text-[var(--muted)]')}
          >
            All
          </button>
        </div>
        <Button variant="outline" size="sm" disabled={resetting} onClick={() => setConfirmOpen(true)}>
          Reset session
        </Button>
      </div>

      {statusQuery.data && statusQuery.data.backends.length > 0 && (
        <div className="flex items-center gap-3 text-xs text-[var(--muted)]">
          {statusQuery.data.backends.map((b) => (
            <span key={b.name} className="inline-flex items-center gap-1.5">
              <span className="h-1.5 w-1.5 rounded-full bg-emerald-500" />
              {b.name}
              {b.default && <span className="text-[10px] uppercase tracking-wide">default</span>}
            </span>
          ))}
        </div>
      )}

      <ConfirmDialog
        open={confirmOpen}
        title="Reset session?"
        description="Starts a new session for new traffic. Nothing is deleted — switch to All to browse full history."
        confirmLabel="Reset"
        onConfirm={handleConfirmReset}
        onCancel={() => setConfirmOpen(false)}
      />
    </div>
  )
}

function Stat({ label, value, danger }: { label: string; value: string; danger?: boolean }) {
  return (
    <div className="flex items-baseline gap-1.5">
      <span className="text-[var(--muted)]">{label}</span>
      <span className={cn('font-medium tabular-nums', danger && 'text-[var(--danger)]')}>{value}</span>
    </div>
  )
}
