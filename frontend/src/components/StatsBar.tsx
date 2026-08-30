import { useCallback, useEffect, useRef, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Settings } from 'lucide-react'
import { api } from '@/api/client'
import { firstRunProbeSaysUnreachable, type BackendHealth, type SessionScope, type StatusBackend } from '@/api/types'
import { ConfigPanel } from '@/components/ConfigPanel'
import { DataPanel } from '@/components/DataPanel'
import { ThemePanel } from '@/components/ThemePanel'
import { Button } from '@/components/ui/button'
import { ConfirmDialog, Dialog } from '@/components/ui/dialog'
import { Mark } from '@/components/ui/Mark'
import { Popover } from '@/components/ui/popover'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { cn } from '@/lib/utils'
import { formatCompactTokenCount, formatMs, formatTokPerSec } from '@/lib/format'

/** §5 — the header panel: mark + wordmark, stat group, session toggle / Reset / gear / backend indicator. */
export function StatsBar({
  scope,
  currentSessionId,
  onScopeChange,
  onReset,
  onDataCleared,
  connected,
}: {
  scope: SessionScope | null
  currentSessionId: number | null
  onScopeChange: (scope: SessionScope) => void
  onReset: () => Promise<void>
  onDataCleared?: (scope: { all: true } | { before: string }) => void
  /** D8 (review §4 risk) — the SSE connection state `useEvents` already tracked but no one displayed. */
  connected: boolean
}) {
  const [confirmOpen, setConfirmOpen] = useState(false)
  const [resetting, setResetting] = useState(false)
  const [settingsOpen, setSettingsOpen] = useState(false)
  const [settingsTab, setSettingsTab] = useState<'data' | 'config' | 'appearance'>('data')

  const statsQuery = useQuery({
    queryKey: ['stats', scope],
    queryFn: () => api.getStats(scope ?? undefined),
    enabled: scope !== null,
    refetchInterval: 5000,
  })

  const statusQuery = useQuery({
    queryKey: ['status'],
    queryFn: api.getStatus,
    staleTime: 5_000,
    refetchInterval: 5_000,
  })

  const stats = statsQuery.data

  // Issue #11 — the default backend stays Ollama, so a machine without Ollama running has
  // a dead default and no signpost: the first thing that says so today is a client's
  // `502 upstream_unreachable`. When the first run's one-shot probe found nothing
  // listening, settings open straight onto Config, whose first control is the
  // known-backend picker (#9) — a cloud-only user configures OpenAI/Claude immediately
  // rather than discovering the problem by failure. Ref-guarded so dismissing it is final:
  // the status query refetches every 5s and must not reopen what the user just closed. The
  // probe is a startup answer that is never refreshed, so a since-observed green supersedes
  // it here too (`firstRunProbeSaysUnreachable`) — a reload after the user started Ollama
  // must not reopen the picker for a backend that is now answering.
  const needsBackendSetup =
    statusQuery.data?.setup.firstRun === true && firstRunProbeSaysUnreachable(statusQuery.data)
  const backendSetupPrompted = useRef(false)
  useEffect(() => {
    if (!needsBackendSetup || backendSetupPrompted.current) return
    backendSetupPrompted.current = true
    setSettingsTab('config')
    setSettingsOpen(true)
  }, [needsBackendSetup])

  // R04 — stable identities, belt-and-suspenders alongside dialog.tsx's own ref-based
  // fix: a fresh inline arrow every render is exactly what turned the focus-trap effect
  // into a per-render focus-steal under the (now-removed) 250ms clock rerender.
  const closeSettings = useCallback(() => setSettingsOpen(false), [])
  const cancelReset = useCallback(() => setConfirmOpen(false), [])

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
        {stats && stats.tokensCachedRead > 0 && (
          <>
            <Divider />
            <Stat label="Cached read" value={formatCompactTokenCount(stats.tokensCachedRead, stats.tokensEstimated)} />
          </>
        )}
        {stats && stats.tokensCachedWrite > 0 && (
          <>
            <Divider />
            <Stat label="Cached write" value={formatCompactTokenCount(stats.tokensCachedWrite, stats.tokensEstimated)} />
          </>
        )}
      </div>

      <div className="ml-auto flex items-center gap-3">
        {!connected && (
          <span
            className="flex items-center gap-1.5 text-xs text-text-muted"
            title="Live-update connection lost — reconnecting…"
          >
            <span className="h-1.5 w-1.5 rounded-full bg-text-muted" aria-hidden="true" />
            Disconnected
          </span>
        )}
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
        <Button variant="ghost" size="icon" aria-label="Settings" title="Settings" onClick={() => setSettingsOpen(true)}>
          <Settings className="h-4 w-4" strokeWidth={1.75} />
        </Button>
        {statusQuery.data && statusQuery.data.backends.length > 0 && (
          <BackendIndicator backends={statusQuery.data.backends} />
        )}
      </div>

      <ConfirmDialog
        open={confirmOpen}
        title="Reset session?"
        description="Starts a new session for new traffic. Nothing is deleted — switch to All to browse full history."
        confirmLabel="Reset"
        onConfirm={handleConfirmReset}
        onCancel={cancelReset}
      />

      <Dialog open={settingsOpen} title="Settings" onClose={closeSettings} widthClassName="w-[620px]">
        <Tabs value={settingsTab} onValueChange={(v) => setSettingsTab(v as 'data' | 'config' | 'appearance')}>
          <TabsList>
            <TabsTrigger value="data">Data</TabsTrigger>
            <TabsTrigger value="config">Config</TabsTrigger>
            <TabsTrigger value="appearance">Appearance</TabsTrigger>
          </TabsList>
          <TabsContent value="data" className="pt-3">
            <DataPanel onCleared={onDataCleared} />
          </TabsContent>
          <TabsContent value="config" className="pt-3">
            <ConfigPanel />
          </TabsContent>
          <TabsContent value="appearance" className="pt-3">
            <ThemePanel />
          </TabsContent>
        </Tabs>
      </Dialog>
    </div>
  )
}

function BackendIndicator({ backends }: { backends: StatusBackend[] }) {
  const defaultBackend = backends.find((backend) => backend.default) ?? backends[0]
  const otherBackends = backends.filter((backend) => backend !== defaultBackend)
  const otherHealth = worstHealth(otherBackends.map((backend) => backend.health))

  return (
    <div className="flex shrink-0 items-center gap-2 text-xs text-text-muted">
      <span className="inline-flex items-center gap-1.5 whitespace-nowrap">
        <BackendDot health={defaultBackend.health} />
        <span className="font-mono text-text-secondary">{defaultBackend.name}</span>
        <span className="text-[10px] uppercase tracking-wide">default</span>
        <HealthText health={defaultBackend.health} />
      </span>
      {otherBackends.length > 0 && (
        <Popover
          label="Backend health"
          trigger={(open, toggle, contentId) => (
            <button
              type="button"
              aria-expanded={open}
              aria-controls={open ? contentId : undefined}
              aria-label={`Show ${otherBackends.length} other backend${otherBackends.length === 1 ? '' : 's'}`}
              onClick={toggle}
              className="inline-flex h-6 items-center gap-1 rounded-control border border-border bg-surface-2 px-2 font-mono text-xs text-text-secondary hover:bg-surface-3 hover:text-text"
            >
              <BackendDot health={otherHealth} />+{otherBackends.length}
            </button>
          )}
        >
          <div className="mb-1 px-1 text-[10px] font-[550] uppercase tracking-[0.06em] text-text-muted">Backends</div>
          <div className="flex flex-col gap-1">
            {backends.map((backend) => (
              <div key={backend.name} className="flex items-center gap-2 rounded-control px-1 py-1 text-xs">
                <BackendDot health={backend.health} />
                <span className="min-w-0 flex-1 truncate font-mono text-text">{backend.name}</span>
                <span className="font-mono text-[10px] text-text-muted">{backend.type}</span>
                {backend.default && <span className="text-[10px] uppercase tracking-wide text-text-secondary">default</span>}
                <HealthText health={backend.health} showTimestamp />
              </div>
            ))}
          </div>
        </Popover>
      )}
    </div>
  )
}

function BackendDot({ health }: { health: BackendHealth }) {
  const className = health.state === 'red'
    ? 'bg-danger'
    : health.state === 'unknown'
      ? 'border border-text-muted bg-transparent'
      : 'bg-ok'

  return (
    <span
      className={cn('h-1.5 w-1.5 shrink-0 rounded-full', className)}
      role="img"
      aria-label={`Backend ${health.state}`}
    />
  )
}

function HealthText({ health, showTimestamp = false }: { health: BackendHealth; showTimestamp?: boolean }) {
  if (health.state === 'unknown') return <span className="text-[10px]">unknown</span>
  const state = health.state === 'red'
    ? <span className="text-[10px] text-danger">unreachable</span>
    : null
  if (!showTimestamp || !health.lastSeenAt) return state

  const seen = new Date(health.lastSeenAt)
  if (Number.isNaN(seen.getTime())) return state
  const time = new Intl.DateTimeFormat(undefined, { hour: '2-digit', minute: '2-digit', hour12: false }).format(seen)
  return <span className="text-[10px]">{state} last seen {time}</span>
}

function worstHealth(health: BackendHealth[]): BackendHealth {
  const rank = { green: 0, unknown: 1, red: 2 } as const
  return health.reduce<BackendHealth>(
    (worst, next) => rank[next.state] > rank[worst.state] ? next : worst,
    { state: 'green', lastSeenAt: null },
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

function Divider() {
  return <div className="h-8 w-px bg-border" aria-hidden="true" />
}
