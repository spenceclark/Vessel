import { useEffect, useMemo, useState } from 'react'
import { ApiError, api } from '@/api/client'
import type { RequestDetail, StatusBackend } from '@/api/types'
import { Button } from '@/components/ui/button'
import { Dialog } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'

export function ReplayDialog({
  detail,
  backends,
  open,
  onClose,
}: {
  detail: RequestDetail
  backends: StatusBackend[]
  open: boolean
  onClose: () => void
}) {
  const allowed = useMemo(() => backends.filter((backend) => compatible(detail, backend)), [backends, detail])
  const initialBackend = allowed.find((item) => item.name === detail.backend)?.name ?? allowed[0]?.name ?? ''
  const [backend, setBackend] = useState(initialBackend)
  const [model, setModel] = useState(detail.model ?? '')
  const [error, setError] = useState<string | null>(null)
  const [sending, setSending] = useState(false)

  useEffect(() => {
    if (!open) return
    setBackend(allowed.find((item) => item.name === detail.backend)?.name ?? allowed[0]?.name ?? '')
    setModel(detail.model ?? '')
    setError(null)
  }, [allowed, detail, open])

  async function submit() {
    setSending(true)
    setError(null)
    try {
      const modelOverride = model.trim() === '' ? null : model
      await api.replay(detail.id, {
        ...(backend !== detail.backend ? { backend } : {}),
        ...(modelOverride !== null && modelOverride !== detail.model ? { model: modelOverride } : {}),
      })
      onClose()
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Replay could not be started.')
    } finally {
      setSending(false)
    }
  }

  const raw = detail.format === 'raw'
  return (
    <Dialog open={open} title={`Replay #${detail.id}`} onClose={onClose}>
      <div className="flex flex-col gap-4 text-sm">
        <p className="text-text-secondary">
          Re-sends the captured request through Vessel. The replay will appear in the live list and keep this request’s tags.
        </p>
        {error && <p className="rounded-control bg-[color-mix(in_srgb,var(--color-danger)_12%,transparent)] p-2 text-danger">{error}</p>}
        <label className="flex flex-col gap-1">
          <span className="text-xs text-text-muted">Backend</span>
          <select className="h-7 rounded-control border border-border bg-surface-2 px-2 font-mono text-sm" value={backend} onChange={(e) => setBackend(e.target.value)}>
            {allowed.map((item) => (
              <option key={item.name} value={item.name}>{item.name} · {item.type}</option>
            ))}
          </select>
        </label>
        <label className="flex flex-col gap-1">
          <span className="text-xs text-text-muted">Model override</span>
          <Input value={model} disabled={raw} onChange={(e) => setModel(e.target.value)} placeholder={detail.model ?? 'model'} className="font-mono" />
          {raw && <span className="text-xs text-text-muted">Raw captures can only be re-sent unchanged to their original backend.</span>}
        </label>
        <div className="flex justify-end gap-2">
          <Button variant="ghost" onClick={onClose} disabled={sending}>Cancel</Button>
          <Button variant="primary" onClick={submit} disabled={sending || allowed.length === 0}>{sending ? 'Starting…' : 'Replay'}</Button>
        </div>
      </div>
    </Dialog>
  )
}

function compatible(detail: RequestDetail, backend: StatusBackend): boolean {
  const same = backend.name.toLowerCase() === detail.backend.toLowerCase()
  const type = backend.type.toLowerCase()
  switch (detail.format) {
    case 'openai-chat': return type === 'openai' || type === 'ollama' || (type === 'auto' && same)
    case 'openai-responses': return type === 'openai' || (type === 'auto' && same)
    case 'anthropic-messages': return type === 'anthropic' || type === 'ollama' || (type === 'auto' && same)
    case 'ollama-chat':
    case 'ollama-generate': return type === 'ollama' || (type === 'auto' && same)
    case 'raw': return same
    default: return false
  }
}
