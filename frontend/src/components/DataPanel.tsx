import { useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { api, ApiError } from '@/api/client'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'

const CONFIRM_WORD = 'DELETE'

/**
 * D6 — Clear all / Clear before date, the product's only destructive surface. Each
 * action is behind a typed-confirmation step (type "DELETE" to enable Confirm) rather
 * than a single click.
 */
export function DataPanel({
  onCleared,
}: {
  onCleared?: (scope: { all: true } | { before: string }, boundaryId: number | null) => void
}) {
  const queryClient = useQueryClient()
  const [mode, setMode] = useState<'idle' | 'all' | 'before'>('idle')
  const [beforeDate, setBeforeDate] = useState('')
  const [confirmText, setConfirmText] = useState('')
  const [busy, setBusy] = useState(false)
  const [message, setMessage] = useState<string | null>(null)

  function reset() {
    setMode('idle')
    setConfirmText('')
  }

  async function runClear(scope: { all: true } | { before: string }) {
    setBusy(true)
    setMessage(null)
    try {
      const result = await api.deleteRequests(scope)
      setMessage(`Deleted ${result.deleted} request${result.deleted === 1 ? '' : 's'}.`)
      // R14a/R23 — report the clear *before* invalidating, so the live-history generation is
      // bumped before the refetch below settles and drains its completion buffer (a buffered
      // completion for a cleared row must not survive that drain). The selected row's own
      // cache and selection are the caller's concern (App owns both).
      onCleared?.(scope, result.boundaryId ?? null)
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

  return (
    <div className="flex flex-col gap-4 text-sm">
      <p className="text-text-muted">
        Deleting captured requests is permanent and cannot be undone. This is the only destructive action in Vessel.
      </p>

      {message && <div className="rounded-control border border-border bg-surface-2 px-3 py-2 text-xs text-text">{message}</div>}

      <div className="flex flex-col gap-2 rounded-control border border-border p-3">
        <div className="font-medium text-text">Clear all requests</div>
        {mode !== 'all' ? (
          <Button variant="destructive" className="w-fit" onClick={() => { setMode('all'); setMessage(null) }}>
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
        <div className="font-medium text-text">Clear before date</div>
        {mode !== 'before' ? (
          <Button className="w-fit" onClick={() => { setMode('before'); setMessage(null) }}>
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
