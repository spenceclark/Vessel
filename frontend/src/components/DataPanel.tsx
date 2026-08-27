import { useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { api, ApiError } from '@/api/client'
import { Button } from '@/components/ui/button'

const CONFIRM_WORD = 'DELETE'

/**
 * D6 — Clear all / Clear before date, the product's only destructive surface. Each
 * action is behind a typed-confirmation step (type "DELETE" to enable Confirm) rather
 * than a single click.
 */
export function DataPanel() {
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
      <p className="text-[var(--muted)]">
        Deleting captured requests is permanent and cannot be undone. This is the only destructive action in Vessel.
      </p>

      {message && <div className="rounded-md border border-[var(--border)] bg-[var(--card)] px-3 py-2 text-xs">{message}</div>}

      <div className="flex flex-col gap-2 rounded-md border border-[var(--border)] p-3">
        <div className="font-medium">Clear all requests</div>
        {mode !== 'all' ? (
          <Button variant="destructive" size="sm" className="w-fit" onClick={() => { setMode('all'); setMessage(null) }}>
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

      <div className="flex flex-col gap-2 rounded-md border border-[var(--border)] p-3">
        <div className="font-medium">Clear before date</div>
        {mode !== 'before' ? (
          <Button variant="outline" size="sm" className="w-fit" onClick={() => { setMode('before'); setMessage(null) }}>
            Clear before…
          </Button>
        ) : (
          <>
            <input
              type="datetime-local"
              value={beforeDate}
              onChange={(e) => setBeforeDate(e.target.value)}
              className="h-7 w-fit rounded-md border border-[var(--border)] bg-transparent px-2 text-xs"
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
      <span className="text-xs text-[var(--muted)]">{label}</span>
      <input
        type="text"
        value={confirmText}
        onChange={(e) => onConfirmTextChange(e.target.value)}
        placeholder={CONFIRM_WORD}
        className="h-7 w-40 rounded-md border border-[var(--border)] bg-transparent px-2 text-xs"
      />
      <div className="flex gap-2">
        <Button variant="destructive" size="sm" disabled={!canConfirm} onClick={onConfirm}>
          {busy ? 'Deleting…' : 'Confirm delete'}
        </Button>
        <Button variant="ghost" size="sm" onClick={onCancel} disabled={busy}>
          Cancel
        </Button>
      </div>
    </div>
  )
}
