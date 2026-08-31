import { useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { api, ApiError } from '@/api/client'
import type { RequestClearScope, SessionDeleteSummary, SessionInfo } from '@/api/types'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'

const CONFIRM_WORD = 'DELETE'

/**
 * D6/#41 — bulk or unbounded deletion lives here and requires typed confirmation.
 * Single-session deletion uses the count-confirm flow in the session picker.
 */
export function DataPanel({
  sessions,
  onCleared,
  onDeleteSessions,
}: {
  sessions: SessionInfo[]
  onCleared?: (scope: RequestClearScope) => void
  onDeleteSessions: (sessionIds: number[]) => Promise<SessionDeleteSummary>
}) {
  const queryClient = useQueryClient()
  const [mode, setMode] = useState<'idle' | 'all' | 'before' | 'session'>('idle')
  const [beforeDate, setBeforeDate] = useState('')
  const [sessionIds, setSessionIds] = useState<number[]>([])
  const [confirmText, setConfirmText] = useState('')
  const [busy, setBusy] = useState(false)
  const [message, setMessage] = useState<string | null>(null)

  function reset() {
    setMode('idle')
    setConfirmText('')
    setSessionIds([])
  }

  function begin(nextMode: 'all' | 'before' | 'session') {
    setMode(nextMode)
    setMessage(null)
    setConfirmText('')
    if (nextMode !== 'session') setSessionIds([])
  }

  async function runSessionDelete() {
    if (sessionIds.length === 0) return
    setBusy(true)
    setMessage(null)
    try {
      const result = await onDeleteSessions(sessionIds)
      const success =
        `Deleted ${result.sessionsDeleted} session${result.sessionsDeleted === 1 ? '' : 's'} and `
        + `${result.requestsDeleted} request${result.requestsDeleted === 1 ? '' : 's'}.`
      const failure = result.failures.length === 0
        ? ''
        : ` Failed to delete ${result.failures.length} session${result.failures.length === 1 ? '' : 's'}: ${result.failures
          .map(({ sessionId, message }) => `${sessions.find((session) => session.id === sessionId)?.name?.trim() || `#${sessionId}`} (${message})`)
          .join(', ')}.`
      setMessage(success + failure)
      reset()
    } catch (err) {
      setMessage(err instanceof ApiError ? err.message : 'Failed to delete session.')
      reset()
    } finally {
      setBusy(false)
    }
  }

  async function runClear(scope: { all: true } | { before: string }) {
    setBusy(true)
    setMessage(null)
    try {
      const result = await api.deleteRequests(scope)
      setMessage(`Deleted ${result.deleted} request${result.deleted === 1 ? '' : 's'}.`)
      // R14a — evict the selected row's stale detail cache / selection (App owns both). The
      // live list + completion-buffer purge is driven separately by the server's in-band
      // `cleared` SSE event (R23/H0a), which orders correctly against completions; this ack is
      // UX only. The refetch below just refreshes stats/facets and the authoritative list.
      onCleared?.(scope)
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: ['requests'] }),
        queryClient.invalidateQueries({ queryKey: ['stats'] }),
        queryClient.invalidateQueries({ queryKey: ['facets'] }),
      ])
      reset()
    } catch (err) {
      setMessage(err instanceof ApiError ? err.message : 'Failed to clear requests.')
    } finally {
      setBusy(false)
    }
  }

  const canConfirm = confirmText === CONFIRM_WORD && !busy
  const deletableSessions = sessions.filter((session) => !session.isCurrent)
  const selectedSessions = deletableSessions.filter((session) => sessionIds.includes(session.id))
  const selectedRequestCount = selectedSessions.reduce((sum, session) => sum + session.requestCount, 0)

  return (
    <div className="flex flex-col gap-4 text-sm">
      <p className="text-text-muted">
        Bulk deletion is permanent and cannot be undone. Review the scope and request counts before confirming.
      </p>

      {message && <div className="rounded-control border border-border bg-surface-2 px-3 py-2 text-xs text-text">{message}</div>}

      <div className="flex flex-col gap-2 rounded-control border border-border p-3">
        <div className="font-medium text-text">Clear all requests</div>
        {mode !== 'all' ? (
          <Button variant="destructive" className="w-fit" onClick={() => begin('all')}>
            Clear all…
          </Button>
        ) : (
          <ConfirmBlock
            label={`Type "${CONFIRM_WORD}" to permanently delete every captured request.`}
            confirmText={confirmText}
            onConfirmTextChange={setConfirmText}
            canConfirm={canConfirm}
            busy={busy}
            onConfirm={() => runClear({ all: true })}
            onCancel={reset}
          />
        )}
      </div>

      <div className="flex flex-col gap-2 rounded-control border border-border p-3">
        <div className="font-medium text-text">Delete sessions</div>
        <p className="text-xs text-text-muted">
          Select one or more sessions to permanently delete their markers and captured requests. Current is protected.
        </p>
        {mode !== 'session' ? (
          <Button
            variant="destructive"
            className="w-fit"
            disabled={deletableSessions.length === 0}
            onClick={() => begin('session')}
          >
            Delete sessions…
          </Button>
        ) : (
          <>
            <div className="max-h-56 overflow-y-auto rounded-control border border-border bg-surface-2 p-1" aria-label="Sessions to delete">
              {sessions.map((session) => (
                <label
                  key={session.id}
                  className="flex items-center gap-2 rounded-control px-2 py-1.5 hover:bg-surface-3 has-disabled:opacity-50"
                >
                  <input
                    type="checkbox"
                    checked={sessionIds.includes(session.id)}
                    disabled={session.isCurrent || busy}
                    onChange={(event) => setSessionIds((selected) => event.target.checked
                      ? [...selected, session.id]
                      : selected.filter((id) => id !== session.id))}
                  />
                  <span className="min-w-0 flex-1 truncate font-mono text-sm text-text">
                    {session.name?.trim() || `Session #${session.id}`} · #{session.id}{session.isCurrent ? ' · current' : ''}
                  </span>
                  <span className="shrink-0 text-xs text-text-muted">
                    {session.requestCount} request{session.requestCount === 1 ? '' : 's'}
                  </span>
                </label>
              ))}
            </div>
            <ConfirmBlock
              label={`Type "${CONFIRM_WORD}" to permanently delete ${selectedSessions.length} session${selectedSessions.length === 1 ? '' : 's'} and ${selectedRequestCount} request${selectedRequestCount === 1 ? '' : 's'}.`}
              confirmText={confirmText}
              onConfirmTextChange={setConfirmText}
              canConfirm={canConfirm && selectedSessions.length > 0}
              busy={busy}
              onConfirm={runSessionDelete}
              onCancel={reset}
            />
          </>
        )}
      </div>

      <div className="flex flex-col gap-2 rounded-control border border-border p-3">
        <div className="font-medium text-text">Clear before date</div>
        {mode !== 'before' ? (
          <Button className="w-fit" onClick={() => begin('before')}>
            Clear before…
          </Button>
        ) : (
          <>
            <Input
              type="datetime-local"
              value={beforeDate}
              onChange={(e) => setBeforeDate(e.target.value)}
              className="w-fit"
            />
            <ConfirmBlock
              label={`Type "${CONFIRM_WORD}" to permanently delete every request before this date.`}
              confirmText={confirmText}
              onConfirmTextChange={setConfirmText}
              canConfirm={canConfirm && beforeDate !== ''}
              busy={busy}
              onConfirm={() => runClear({ before: new Date(beforeDate).toISOString() })}
              onCancel={reset}
            />
          </>
        )}
      </div>
    </div>
  )
}

function ConfirmBlock({
  label,
  confirmText,
  onConfirmTextChange,
  canConfirm,
  busy,
  onConfirm,
  onCancel,
}: {
  label: string
  confirmText: string
  onConfirmTextChange: (v: string) => void
  canConfirm: boolean
  busy: boolean
  onConfirm: () => void
  onCancel: () => void
}) {
  return (
    <div className="flex flex-col gap-2">
      <span className="text-xs text-text-muted">{label}</span>
      <Input
        type="text"
        value={confirmText}
        onChange={(e) => onConfirmTextChange(e.target.value)}
        placeholder={CONFIRM_WORD}
        className="w-40"
      />
      <div className="flex gap-2">
        <Button variant="destructive" disabled={!canConfirm} onClick={onConfirm}>
          {busy ? 'Deleting…' : 'Confirm delete'}
        </Button>
        <Button variant="ghost" onClick={onCancel} disabled={busy}>
          Cancel
        </Button>
      </div>
    </div>
  )
}
