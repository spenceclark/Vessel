import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Settings } from 'lucide-react'
import { api } from '@/api/client'
import type { SessionScope } from '@/api/types'
import { ConfigPanel } from '@/components/ConfigPanel'
import { DataPanel } from '@/components/DataPanel'
import { Button } from '@/components/ui/button'
import { ConfirmDialog, Dialog } from '@/components/ui/dialog'
import { Mark } from '@/components/ui/Mark'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { cn } from '@/lib/utils'
import { formatCompactTokenCount, formatMs, formatTokPerSec } from '@/lib/format'

/** §5 — the header panel: mark + wordmark, stat group, session toggle / Reset / gear / backend indicator. */
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
  const [settingsOpen, setSettingsOpen] = useState(false)
  const [settingsTab, setSettingsTab] = useState<'data' | 'config'>('data')

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
    <div className="flex flex-wrap items-center gap-x-6 gap-y-2 rounded-panel border border-border bg-surface px-4 py-3 shadow-panel">
      <div className="flex items-center gap-2">
        <Mark size={22} />
        <span className="text-lg text-text" style={{ fontWeight: 650, letterSpacing: '-0.02em' }}>
          vessel
        </span>
      </div>

      <div className="flex items-center gap-4" aria-live="polite">
        <Stat label="Requests" value={stats ? String(stats.total) : '—'} />
        <Divider />
        <Stat label="Failed" value={stats ? String(stats.failed) : '—'} danger={!!stats && stats.failed > 0} />
        <Divider />
        <Stat label="Avg latency" value={formatMs(stats?.avgDurationMs)} />
        <Divider />
        <Stat label="Avg tok/s" value={formatTokPerSec(stats?.avgTokPerSec)} />
        <Divider />
        <Stat label="Avg TTFT" value={formatMs(stats?.avgTtftMs)} />
        <Divider />
        <Stat
          label="Tokens in"
          value={stats ? formatCompactTokenCount(stats.tokensIn, stats.tokensEstimated) : '—'}
        />
        <Stat
          label="Tokens out"
          value={stats ? formatCompactTokenCount(stats.tokensOut, stats.tokensEstimated) : '—'}
        />
        {stats && stats.tokensCachedRead + stats.tokensCachedWrite > 0 && (
          <>
            <Divider />
            <CachedStat read={stats.tokensCachedRead} write={stats.tokensCachedWrite} />
          </>
        )}
      </div>

      <div className="ml-auto flex items-center gap-3">
        <Tabs
          value={scope === 'all' ? 'all' : 'current'}
          onValueChange={(v) => {
            if (v === 'all') onScopeChange('all')
            else if (currentSessionId !== null) onScopeChange(currentSessionId)
          }}
        >
          <TabsList>
            <TabsTrigger value="current" disabled={currentSessionId === null}>
              Current
            </TabsTrigger>
            <TabsTrigger value="all">All</TabsTrigger>
          </TabsList>
        </Tabs>
        <Button disabled={resetting} onClick={() => setConfirmOpen(true)}>
          Reset session
        </Button>
        <Button variant="ghost" size="icon" aria-label="Settings" title="Data & config" onClick={() => setSettingsOpen(true)}>
          <Settings className="h-4 w-4" strokeWidth={1.75} />
        </Button>
      </div>

      {statusQuery.data && statusQuery.data.backends.length > 0 && (
        <div className="flex items-center gap-3 text-xs text-text-muted">
          {statusQuery.data.backends.map((b) => (
            <span key={b.name} className="inline-flex items-center gap-1.5">
              <span className="h-1.5 w-1.5 rounded-full bg-ok" />
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

      <Dialog open={settingsOpen} title="Data & config" onClose={() => setSettingsOpen(false)} widthClassName="w-[620px]">
        <Tabs value={settingsTab} onValueChange={(v) => setSettingsTab(v as 'data' | 'config')}>
          <TabsList>
            <TabsTrigger value="data">Data</TabsTrigger>
            <TabsTrigger value="config">Config</TabsTrigger>
          </TabsList>
          <TabsContent value="data" className="pt-3">
            <DataPanel />
          </TabsContent>
          <TabsContent value="config" className="pt-3">
            <ConfigPanel />
          </TabsContent>
        </Tabs>
      </Dialog>
    </div>
  )
}

function Stat({ label, value, danger }: { label: string; value: string; danger?: boolean }) {
  return (
    <div className="flex flex-col gap-0.5">
      <span className="text-xs font-[550] uppercase tracking-[0.06em] text-text-muted">{label}</span>
      <span className={cn('text-stat font-semibold tabular-nums', danger ? 'text-danger' : 'text-text')}>{value}</span>
    </div>
  )
}

/**
 * ui-spec.md §9.1 — conditional slot (only rendered when read+write > 0 in scope):
 * "12.4k r · 310 w", the unit letters muted so the digits carry the emphasis. A zero
 * half is omitted rather than shown as "0 r"/"0 w" — nothing to report on that side.
 */
function CachedStat({ read, write }: { read: number; write: number }) {
  const parts: string[] = []
  if (read > 0) parts.push(`${formatCompactTokenCount(read, false)} r`)
  if (write > 0) parts.push(`${formatCompactTokenCount(write, false)} w`)

  return (
    <div className="flex flex-col gap-0.5">
      <span className="text-xs font-[550] uppercase tracking-[0.06em] text-text-muted">Cached</span>
      <span className="text-stat font-semibold tabular-nums text-text">
        {parts.map((part, i) => {
          const [value, unit] = part.split(' ')
          return (
            <span key={i}>
              {i > 0 && <span className="text-text-muted"> · </span>}
              {value}
              <span className="text-text-muted">{' ' + unit}</span>
            </span>
          )
        })}
      </span>
    </div>
  )
}

function Divider() {
  return <div className="h-8 w-px bg-border" aria-hidden="true" />
}
