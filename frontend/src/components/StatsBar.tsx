import { useCallback, useEffect, useRef, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { ChevronDown, Search, Settings, Trash2 } from 'lucide-react'
import { api } from '@/api/client'
import { firstRunProbeSaysUnreachable, type BackendHealth, type RequestClearScope, type SessionDeleteSummary, type SessionInfo, type SessionScope, type StatusBackend } from '@/api/types'
import { ConfigPanel } from '@/components/ConfigPanel'
import { DataPanel } from '@/components/DataPanel'
import { ThemePanel } from '@/components/ThemePanel'
import { Button } from '@/components/ui/button'
import { ConfirmDialog, Dialog } from '@/components/ui/dialog'
import { Mark } from '@/components/ui/Mark'
import { Input } from '@/components/ui/input'
import { Popover } from '@/components/ui/popover'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { cn } from '@/lib/utils'
import { formatCompactTokenCount, formatMs, formatTokPerSec } from '@/lib/format'

/** §5 — the header panel: mark + wordmark, view toggle, stat group, session toggle / Reset / gear / backend indicator. */
export function StatsBar({
  scope,
  sessions,
  onScopeChange,
  onReset,
  onDataCleared,
  onDeleteSessions,
  connected,
  view,
  onViewChange,
}: {
  scope: SessionScope | null
  sessions: SessionInfo[]
  onScopeChange: (scope: SessionScope) => void
  onReset: () => Promise<void>
  onDataCleared?: (scope: RequestClearScope) => void
  onDeleteSessions: (sessionIds: number[]) => Promise<SessionDeleteSummary>
  /** D8 (review §4 risk) — the SSE connection state `useEvents` already tracked but no one displayed. */
  connected: boolean
  /** Phase 7 D10 — which main-area view the header toggles. */
  view: 'history' | 'reports'
  onViewChange: (view: 'history' | 'reports') => void
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
    // gap-x-3 (not the panel-to-panel gap-x-6): these are individual header controls,
    // not separate panels, and §4's own "between controls 8" rule is the right budget —
    // the header wraps to a second line past ~1550px of stats + controls otherwise
    // (found live: 9 stats + the view toggle overflows at the app's own 1600px max-width).
    <div className="flex flex-wrap items-center gap-x-3 gap-y-2 rounded-panel border border-border bg-surface px-4 py-3 shadow-panel">
      <div className="flex items-center gap-2">
        <Mark size={22} />
        <span className="text-lg text-text" style={{ fontWeight: 650, letterSpacing: '-0.02em' }}>
          vessel
        </span>
      </div>

      {/* Phase 7 D10 — the History / Reports view toggle (§6 segmented control). The
          header stays shared across both views, so scope, stats and session controls
          carry over — which the reports read. Tighter padding than the default
          TabsTrigger: this control competes for space with up to 9 stat groups on the
          same line, per the header-width finding above. */}
      <Tabs value={view} onValueChange={(v) => onViewChange(v as 'history' | 'reports')}>
        <TabsList aria-label="View">
          <TabsTrigger value="history" className="px-2 text-xs">History</TabsTrigger>
          {/* Reports needs a resolved scope (App.tsx renders History as a fallback
              otherwise) — disabled during the brief initial-load window keeps the toggle's
              own selected state from lying about what's on screen. */}
          <TabsTrigger value="reports" className="px-2 text-xs" disabled={scope === null}>Reports</TabsTrigger>
        </TabsList>
      </Tabs>

      {/* §4 "between controls 8": the generous panel-to-panel gap-4 here was what a
          9-stat session (both cached columns present) needed to not overflow the header's
          own max-width alongside the view toggle above. */}
      <div className="flex items-center gap-2" aria-live="polite">
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
        <SessionPicker
          scope={scope}
          sessions={sessions}
          onScopeChange={onScopeChange}
          onDeleteSession={(sessionId) => onDeleteSessions([sessionId])}
        />
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
        description="Starts a new session for headerless traffic. Nothing is deleted — choose another session or All sessions to browse history."
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
            <DataPanel sessions={sessions} onCleared={onDataCleared} onDeleteSessions={onDeleteSessions} />
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

function sessionLabel(session: SessionInfo) {
  const name = session.name?.trim() || `Session #${session.id}`
  return `${name} · #${session.id}${session.isCurrent ? ' · current' : ''}`
}

const RecentSessionLimit = 15

function SessionPicker({
  scope,
  sessions,
  onScopeChange,
  onDeleteSession,
}: {
  scope: SessionScope | null
  sessions: SessionInfo[]
  onScopeChange: (scope: SessionScope) => void
  onDeleteSession: (sessionId: number) => Promise<SessionDeleteSummary>
}) {
  const [query, setQuery] = useState('')
  const [pendingDelete, setPendingDelete] = useState<SessionInfo | null>(null)
  const [deleting, setDeleting] = useState(false)
  const [deleteError, setDeleteError] = useState<string | null>(null)
  const current = sessions.find((session) => session.isCurrent)
  const selected = typeof scope === 'number' ? sessions.find((session) => session.id === scope) : undefined
  const normalizedQuery = query.trim().toLocaleLowerCase()
  const candidates = sessions.filter((session) => !session.isCurrent)
  const visible = normalizedQuery
    ? candidates.filter((session) => sessionLabel(session).toLocaleLowerCase().includes(normalizedQuery))
    : candidates.slice(0, RecentSessionLimit)
  const triggerLabel = scope === 'all' ? 'All sessions' : selected ? sessionLabel(selected) : 'Choose session'

  async function confirmDelete(close: () => void) {
    if (!pendingDelete || pendingDelete.isCurrent) return
    setDeleting(true)
    setDeleteError(null)
    try {
      const result = await onDeleteSession(pendingDelete.id)
      if (result.failures.length > 0) {
        setDeleteError(result.failures[0].message)
        return
      }
      setPendingDelete(null)
      close()
    } catch (error) {
      setDeleteError(error instanceof Error ? error.message : 'Failed to delete session.')
    } finally {
      setDeleting(false)
    }
  }

  return (
    <Popover
      label="Choose session"
      contentClassName="w-80"
      onOpenChange={(open) => {
        if (!open) {
          setQuery('')
          setPendingDelete(null)
          setDeleteError(null)
        }
      }}
      trigger={(open, toggle, contentId) => (
        <button
          type="button"
          aria-label="Session"
          aria-expanded={open}
          aria-controls={open ? contentId : undefined}
          disabled={scope === null}
          onClick={toggle}
          className="flex h-7 max-w-56 items-center gap-2 rounded-control border border-border bg-surface-2 px-2 text-sm text-text hover:bg-surface-3 disabled:opacity-50"
        >
          <span className="min-w-0 truncate font-mono">{triggerLabel}</span>
          <ChevronDown className="h-3.5 w-3.5 shrink-0 text-text-muted" strokeWidth={1.75} aria-hidden="true" />
        </button>
      )}
    >
      {(close) => (
        <div className="flex flex-col gap-2">
          <Input
            autoFocus
            aria-label="Filter sessions"
            placeholder="Filter sessions…"
            value={query}
            onChange={(event) => setQuery(event.target.value)}
            icon={<Search strokeWidth={1.75} />}
          />
          {pendingDelete ? (
            <div className="flex flex-col gap-3 rounded-control border border-danger/40 bg-surface-2 p-3" role="alertdialog" aria-label="Confirm session deletion">
              <p className="text-sm text-text">
                Delete <span className="font-mono">{pendingDelete.name?.trim() || `Session #${pendingDelete.id}`}</span>
                {' — '}{pendingDelete.requestCount} request{pendingDelete.requestCount === 1 ? '' : 's'}?
              </p>
              <p className="text-xs text-text-muted">The session marker and captured requests will be permanently deleted.</p>
              {deleteError && <p className="text-xs text-danger">{deleteError}</p>}
              <div className="flex gap-2">
                <Button variant="destructive" disabled={deleting} onClick={() => void confirmDelete(close)}>
                  {deleting ? 'Deleting…' : 'Delete'}
                </Button>
                <Button variant="ghost" disabled={deleting} onClick={() => { setPendingDelete(null); setDeleteError(null) }}>
                  Cancel
                </Button>
              </div>
            </div>
          ) : (
            <>
              <div className="flex flex-col gap-1" role="listbox" aria-label="Sessions">
                <SessionOption
                  label="All sessions"
                  context="Full captured history"
                  selected={scope === 'all'}
                  onClick={() => { onScopeChange('all'); close() }}
                />
                {current && (
                  <SessionOption
                    label={sessionLabel(current)}
                    context={sessionContext(current)}
                    selected={scope === current.id}
                    onClick={() => { onScopeChange(current.id); close() }}
                  />
                )}
              </div>
              <div className="h-px bg-border" aria-hidden="true" />
              <div className="max-h-72 overflow-y-auto" role="listbox" aria-label="Recent sessions">
                {visible.length === 0 ? (
                  <p className="px-2 py-3 text-center text-xs text-text-muted">No matching sessions</p>
                ) : visible.map((session) => (
                  <SessionOption
                    key={session.id}
                    label={sessionLabel(session)}
                    context={sessionContext(session)}
                    selected={scope === session.id}
                    onClick={() => { onScopeChange(session.id); close() }}
                    onDelete={() => { setPendingDelete(session); setDeleteError(null) }}
                  />
                ))}
              </div>
            </>
          )}
        </div>
      )}
    </Popover>
  )
}

function SessionOption({
  label,
  context,
  selected,
  onClick,
  onDelete,
}: {
  label: string
  context: string
  selected: boolean
  onClick: () => void
  onDelete?: () => void
}) {
  return (
    <div
      role="option"
      aria-selected={selected}
      onClick={onClick}
      onKeyDown={(event) => {
        if (event.target !== event.currentTarget) return
        if (event.key === 'Enter' || event.key === ' ') {
          event.preventDefault()
          onClick()
        }
      }}
      tabIndex={0}
      className={cn(
        'flex w-full items-center rounded-control px-2 py-1.5 text-left hover:bg-surface-2',
        selected && 'bg-surface-3',
      )}
    >
      <span className="flex min-w-0 flex-1 flex-col">
        <span className="truncate font-mono text-sm text-text">{label}</span>
        <span className="text-xs text-text-muted">{context}</span>
      </span>
      {onDelete && (
        <button
          type="button"
          aria-label={`Delete ${label}`}
          title="Delete session"
          onClick={(event) => { event.stopPropagation(); onDelete() }}
          className="rounded-control p-1 text-text-muted hover:bg-surface-3 hover:text-danger"
        >
          <Trash2 className="h-3.5 w-3.5" strokeWidth={1.75} aria-hidden="true" />
        </button>
      )}
    </div>
  )
}

function sessionContext(session: SessionInfo) {
  const count = `${session.requestCount} request${session.requestCount === 1 ? '' : 's'}`
  return `${count} · ${relativeDate(session.lastRequestAt ?? session.startedAt)}`
}

function relativeDate(iso: string) {
  const time = new Date(iso).getTime()
  if (!Number.isFinite(time)) return iso
  const seconds = Math.max(0, Math.floor((Date.now() - time) / 1000))
  if (seconds < 60) return 'just now'
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m ago`
  if (seconds < 86_400) return `${Math.floor(seconds / 3600)}h ago`
  if (seconds < 2_592_000) return `${Math.floor(seconds / 86_400)}d ago`
  return new Date(time).toLocaleDateString()
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
