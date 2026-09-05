import { useEffect, useMemo, useState } from 'react'
import { ApiError, api } from '@/api/client'
import { MAX_REPLAY_VARIATIONS, type ReplayVariation, type RequestDetail, type StatusBackend } from '@/api/types'
import { Button } from '@/components/ui/button'
import { Dialog } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'

type Mode = 'single' | 'models' | 'params'

type ModelRow = { backend: string; model: string }

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
  const defaultBackend = allowed.find((item) => item.name === detail.backend)?.name ?? allowed[0]?.name ?? ''
  const params = useMemo(() => PARAM_PLACEMENT[detail.format] ?? [], [detail.format])
  const raw = detail.format === 'raw'

  const [mode, setMode] = useState<Mode>('single')
  const [backend, setBackend] = useState(defaultBackend)
  const [model, setModel] = useState(detail.model ?? '')
  const [rows, setRows] = useState<ModelRow[]>([{ backend: defaultBackend, model: detail.model ?? '' }])
  const [paramName, setParamName] = useState(params[0]?.name ?? '')
  const [paramValues, setParamValues] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [sending, setSending] = useState(false)

  useEffect(() => {
    if (!open) return
    const initial = allowed.find((item) => item.name === detail.backend)?.name ?? allowed[0]?.name ?? ''
    /* oxlint-disable react/set-state-in-effect -- opening a different replay target must reset the transient form fields. */
    setMode('single')
    setBackend(initial)
    setModel(detail.model ?? '')
    setRows([{ backend: initial, model: detail.model ?? '' }])
    setParamName((PARAM_PLACEMENT[detail.format] ?? [])[0]?.name ?? '')
    setParamValues('')
    setError(null)
    /* oxlint-enable react/set-state-in-effect */
  }, [allowed, detail, open])

  // #48 — every mode produces the same `variations` list; the one-axis guard is that a mode
  // varies models or params, never both, and it lives here rather than in the API.
  const variations = useMemo((): ReplayVariation[] => {
    if (mode === 'single') {
      return [{
        ...(backend !== detail.backend ? { backend } : {}),
        ...(model.trim() !== '' && model !== detail.model ? { model: model.trim() } : {}),
      }]
    }
    if (mode === 'models') {
      return rows
        .filter((row) => row.backend !== '')
        .map((row) => ({
          ...(row.backend !== detail.backend ? { backend: row.backend } : {}),
          ...(row.model.trim() !== '' ? { model: row.model.trim() } : {}),
        }))
    }
    const placement = params.find((item) => item.name === paramName)
    if (!placement) return []
    return parseValues(paramValues).map((value) => ({
      ...(backend !== detail.backend ? { backend } : {}),
      ...(model.trim() !== '' && model !== detail.model ? { model: model.trim() } : {}),
      params: placement.under === 'options' ? { options: { [placement.name]: value } } : { [placement.name]: value },
    }))
  }, [mode, backend, model, rows, params, paramName, paramValues, detail])

  const targets = variations.map((variation) => variation.backend ?? detail.backend)
  const keyed = [...new Set(targets.filter((name) => backends.find((item) => item.name === name)?.requiresAuth))]
  const paidCount = targets.filter((name) => backends.find((item) => item.name === name)?.requiresAuth).length
  const overCap = variations.length > MAX_REPLAY_VARIATIONS

  async function submit() {
    setSending(true)
    setError(null)
    try {
      await api.replay(detail.id, mode === 'single' ? variations[0] : { variations })
      onClose()
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Replay could not be started.')
    } finally {
      setSending(false)
    }
  }

  return (
    <Dialog open={open} title={`Replay #${detail.id}`} onClose={onClose}>
      <div className="flex flex-col gap-4 text-sm">
        <p className="text-text-secondary">
          Re-sends the captured request through Vessel. The replay will appear in the live list and keep this request’s tags.
        </p>
        {error && <p className="rounded-control bg-[color-mix(in_srgb,var(--color-danger)_12%,transparent)] p-2 text-danger">{error}</p>}

        <div className="flex gap-1" role="group" aria-label="Replay mode">
          {(['single', 'models', 'params'] as const).map((item) => (
            <Button
              key={item}
              variant={mode === item ? 'primary' : 'ghost'}
              disabled={raw && item !== 'single'}
              onClick={() => setMode(item)}
            >
              {item === 'single' ? 'Single' : item === 'models' ? 'Models' : 'Params'}
            </Button>
          ))}
        </div>

        {mode === 'models' ? (
          <div className="flex flex-col gap-2">
            <span className="text-xs text-text-muted">Models ({rows.length})</span>
            {rows.map((row, index) => (
              <div key={index} className="flex gap-2">
                <BackendSelect allowed={allowed} value={row.backend} onChange={(next) => setRows(rows.map((r, i) => (i === index ? { ...r, backend: next } : r)))} />
                <Input
                  value={row.model}
                  placeholder={detail.model ?? 'model'}
                  className="font-mono"
                  aria-label={`Model ${index + 1}`}
                  onChange={(e) => setRows(rows.map((r, i) => (i === index ? { ...r, model: e.target.value } : r)))}
                />
                <Button variant="ghost" disabled={rows.length === 1} onClick={() => setRows(rows.filter((_, i) => i !== index))}>Remove</Button>
              </div>
            ))}
            <div>
              <Button
                variant="ghost"
                disabled={rows.length >= MAX_REPLAY_VARIATIONS}
                onClick={() => setRows([...rows, { backend: defaultBackend, model: '' }])}
              >
                Add model
              </Button>
            </div>
          </div>
        ) : mode === 'params' ? (
          <div className="flex flex-col gap-3">
            <label className="flex flex-col gap-1">
              <span className="text-xs text-text-muted">Parameter</span>
              <select className="h-7 rounded-control border border-border bg-surface-2 px-2 font-mono text-sm" value={paramName} onChange={(e) => setParamName(e.target.value)}>
                {params.map((item) => <option key={item.name} value={item.name}>{item.name}</option>)}
              </select>
            </label>
            <label className="flex flex-col gap-1">
              <span className="text-xs text-text-muted">Values</span>
              <Input value={paramValues} onChange={(e) => setParamValues(e.target.value)} placeholder="0.2, 0.7, 1.0" className="font-mono" />
              <span className="text-xs text-text-muted">One replay per comma-separated value, all against the backend and model below.</span>
            </label>
            <BackendField allowed={allowed} value={backend} onChange={setBackend} />
            <ModelField detail={detail} value={model} onChange={setModel} disabled={false} />
          </div>
        ) : (
          <>
            <BackendField allowed={allowed} value={backend} onChange={setBackend} />
            <ModelField detail={detail} value={model} onChange={setModel} disabled={raw} />
          </>
        )}

        {mode !== 'single' && (
          // #48 — the count is the confirmation: fanning to N keyed backends is N paid calls,
          // and that must be visible before firing rather than discovered on the bill.
          <p className="text-xs text-text-muted">
            {overCap
              ? `Too many variations: ${variations.length} of at most ${MAX_REPLAY_VARIATIONS}.`
              : `Sends ${variations.length} request${variations.length === 1 ? '' : 's'}${keyed.length > 0 ? ` · ${paidCount} to keyed backends: ${keyed.join(', ')}` : ''}`}
          </p>
        )}

        <div className="flex justify-end gap-2">
          <Button variant="ghost" onClick={onClose} disabled={sending}>Cancel</Button>
          <Button variant="primary" onClick={submit} disabled={sending || allowed.length === 0 || variations.length === 0 || overCap}>
            {sending ? 'Starting…' : mode === 'single' ? 'Replay' : `Replay ×${variations.length}`}
          </Button>
        </div>
      </div>
    </Dialog>
  )
}

function BackendSelect({ allowed, value, onChange }: { allowed: StatusBackend[]; value: string; onChange: (value: string) => void }) {
  return (
    <select className="h-7 rounded-control border border-border bg-surface-2 px-2 font-mono text-sm" value={value} onChange={(e) => onChange(e.target.value)} aria-label="Backend">
      {allowed.map((item) => <option key={item.name} value={item.name}>{item.name} · {item.type}</option>)}
    </select>
  )
}

function BackendField({ allowed, value, onChange }: { allowed: StatusBackend[]; value: string; onChange: (value: string) => void }) {
  return (
    <label className="flex flex-col gap-1">
      <span className="text-xs text-text-muted">Backend</span>
      <BackendSelect allowed={allowed} value={value} onChange={onChange} />
    </label>
  )
}

function ModelField({ detail, value, onChange, disabled }: { detail: RequestDetail; value: string; onChange: (value: string) => void; disabled: boolean }) {
  return (
    <label className="flex flex-col gap-1">
      <span className="text-xs text-text-muted">Model override</span>
      <Input value={value} disabled={disabled} onChange={(e) => onChange(e.target.value)} placeholder={detail.model ?? 'model'} className="font-mono" />
      {disabled && <span className="text-xs text-text-muted">Raw captures can only be re-sent unchanged to their original backend.</span>}
    </label>
  )
}

/**
 * #48 — where each tunable parameter lives per format. Frontend data by design: the endpoint
 * takes a merge patch and stays format-agnostic, exactly as `OPENAI_CHAT_RENAME_RULES` in
 * CompareView mirrors #28's rename rules by hand rather than asking the server.
 */
const PARAM_PLACEMENT: Record<string, { name: string; under?: 'options' }[]> = {
  'openai-chat': ['temperature', 'top_p', 'max_tokens', 'max_completion_tokens', 'presence_penalty', 'frequency_penalty', 'seed'].map((name) => ({ name })),
  'openai-responses': ['temperature', 'top_p', 'max_tokens', 'max_completion_tokens', 'presence_penalty', 'frequency_penalty', 'seed'].map((name) => ({ name })),
  'anthropic-messages': ['temperature', 'top_p', 'top_k', 'max_tokens'].map((name) => ({ name })),
  'ollama-chat': ['temperature', 'top_p', 'top_k', 'num_predict', 'repeat_penalty', 'seed'].map((name) => ({ name, under: 'options' as const })),
  'ollama-generate': ['temperature', 'top_p', 'top_k', 'num_predict', 'repeat_penalty', 'seed'].map((name) => ({ name, under: 'options' as const })),
}

/** Comma-separated values, each read as JSON where it parses (numbers, true/false) and as a string otherwise. */
function parseValues(input: string): unknown[] {
  return input
    .split(',')
    .map((part) => part.trim())
    .filter((part) => part !== '')
    .map((part) => {
      try {
        return JSON.parse(part) as unknown
      } catch {
        return part
      }
    })
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
