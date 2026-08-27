import { useEffect, useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { api, ApiError } from '@/api/client'
import type { BackendConfigDto, VesselConfigDto } from '@/api/types'
import { Button } from '@/components/ui/button'

const INPUT = 'h-7 rounded-md border border-[var(--border)] bg-transparent px-2 text-xs'

/**
 * D7 — the config editor: backends table (add/remove/default/injectStreamUsage),
 * retention/capture/slow-TTFT numbers, listen (flagged as restart-required). Client-side
 * required-field checks before Save; the server's 400 validation message surfaces
 * verbatim on failure.
 */
export function ConfigPanel() {
  const queryClient = useQueryClient()
  const configQuery = useQuery({ queryKey: ['config'], queryFn: api.getConfig })
  const [draft, setDraft] = useState<VesselConfigDto | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)
  const [restartRequired, setRestartRequired] = useState<string[]>([])

  useEffect(() => {
    if (configQuery.data && draft === null) setDraft(configQuery.data)
  }, [configQuery.data, draft])

  if (!draft) {
    return <div className="text-sm text-[var(--muted)]">Loading…</div>
  }

  function updateBackend(name: string, patch: Partial<BackendConfigDto>) {
    setDraft((d) => (d ? { ...d, backends: { ...d.backends, [name]: { ...d.backends[name], ...patch } } } : d))
  }

  function renameBackend(oldName: string, newName: string) {
    if (!newName || newName === oldName) return
    setDraft((d) => {
      if (!d || !(oldName in d.backends)) return d
      const backends = { ...d.backends }
      const cfg = backends[oldName]
      delete backends[oldName]
      backends[newName] = cfg
      return { ...d, backends, defaultBackend: d.defaultBackend === oldName ? newName : d.defaultBackend }
    })
  }

  function removeBackend(name: string) {
    setDraft((d) => {
      if (!d) return d
      const backends = { ...d.backends }
      delete backends[name]
      const remaining = Object.keys(backends)
      return {
        ...d,
        backends,
        defaultBackend: d.defaultBackend === name ? (remaining[0] ?? '') : d.defaultBackend,
      }
    })
  }

  function addBackend() {
    setDraft((d) => {
      if (!d) return d
      let name = 'new-backend'
      let n = 2
      while (name in d.backends) name = `new-backend-${n++}`
      return { ...d, backends: { ...d.backends, [name]: { baseUrl: '', type: 'auto' } } }
    })
  }

  function validate(config: VesselConfigDto): string | null {
    const names = Object.keys(config.backends)
    if (names.length === 0) return 'At least one backend is required.'
    for (const [name, backend] of Object.entries(config.backends)) {
      if (!name.trim()) return 'Backend names cannot be empty.'
      if (!backend.baseUrl.trim()) return `Backend "${name}" needs a base URL.`
    }
    if (!(config.defaultBackend in config.backends)) return 'Default backend must be one of the configured backends.'
    if (config.retention.maxRequests <= 0) return 'Max requests must be positive.'
    if (config.retention.maxDbSizeMb <= 0) return 'Max DB size (MB) must be positive.'
    if (config.capture.maxBodyMb <= 0) return 'Max body size (MB) must be positive.'
    if (config.warnings.slowTtftMs < 0) return 'Slow TTFT threshold cannot be negative.'
    return null
  }

  async function handleSave() {
    if (!draft) return
    const clientError = validate(draft)
    if (clientError) {
      setError(clientError)
      return
    }

    setSaving(true)
    setError(null)
    try {
      const result = await api.putConfig(draft)
      setRestartRequired(result.restartRequired)
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: ['status'] }),
        queryClient.invalidateQueries({ queryKey: ['facets'] }),
        queryClient.invalidateQueries({ queryKey: ['config'] }),
      ])
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Failed to save config.')
    } finally {
      setSaving(false)
    }
  }

  return (
    <div className="flex flex-col gap-4 text-sm">
      {restartRequired.length > 0 && (
        <div className="rounded-md border border-[var(--warning)]/40 bg-[var(--warning)]/10 px-3 py-2 text-xs text-[var(--warning)]">
          Saved. {restartRequired.join(', ')} change{restartRequired.length === 1 ? 's' : ''} on next start.
        </div>
      )}
      {error && (
        <div className="rounded-md border border-[var(--danger)]/40 bg-[var(--danger)]/10 px-3 py-2 text-xs text-[var(--danger)]">
          {error}
        </div>
      )}

      <div>
        <div className="mb-1.5 flex items-center justify-between">
          <h3 className="text-xs font-semibold uppercase tracking-wide text-[var(--muted)]">Backends</h3>
          <Button variant="outline" size="sm" onClick={addBackend}>
            Add backend
          </Button>
        </div>
        <div className="flex flex-col gap-2">
          {Object.entries(draft.backends).map(([name, backend]) => (
            <BackendRow
              key={name}
              name={name}
              backend={backend}
              isDefault={draft.defaultBackend === name}
              canRemove={Object.keys(draft.backends).length > 1}
              onRename={(next) => renameBackend(name, next)}
              onUpdate={(patch) => updateBackend(name, patch)}
              onMakeDefault={() => setDraft((d) => (d ? { ...d, defaultBackend: name } : d))}
              onRemove={() => removeBackend(name)}
            />
          ))}
        </div>
      </div>

      <div>
        <h3 className="mb-1.5 text-xs font-semibold uppercase tracking-wide text-[var(--muted)]">Retention & capture</h3>
        <div className="grid grid-cols-2 gap-3">
          <NumberField
            label="Max requests"
            value={draft.retention.maxRequests}
            onChange={(v) => setDraft((d) => (d ? { ...d, retention: { ...d.retention, maxRequests: v } } : d))}
          />
          <NumberField
            label="Max DB size (MB)"
            value={draft.retention.maxDbSizeMb}
            onChange={(v) => setDraft((d) => (d ? { ...d, retention: { ...d.retention, maxDbSizeMb: v } } : d))}
          />
          <NumberField
            label="Max body size (MB)"
            value={draft.capture.maxBodyMb}
            onChange={(v) => setDraft((d) => (d ? { ...d, capture: { ...d.capture, maxBodyMb: v } } : d))}
          />
          <NumberField
            label="Slow TTFT threshold (ms)"
            value={draft.warnings.slowTtftMs}
            onChange={(v) => setDraft((d) => (d ? { ...d, warnings: { ...d.warnings, slowTtftMs: v } } : d))}
          />
        </div>
      </div>

      <div>
        <h3 className="mb-1.5 text-xs font-semibold uppercase tracking-wide text-[var(--muted)]">Listen address</h3>
        <input
          type="text"
          value={draft.listen}
          onChange={(e) => setDraft((d) => (d ? { ...d, listen: e.target.value } : d))}
          className={`${INPUT} w-48`}
        />
        <p className="mt-1 text-xs text-[var(--muted)]">Changes on next start — not applied live.</p>
      </div>

      <div>
        <Button onClick={handleSave} disabled={saving}>
          {saving ? 'Saving…' : 'Save'}
        </Button>
      </div>
    </div>
  )
}

function BackendRow({
  name,
  backend,
  isDefault,
  canRemove,
  onRename,
  onUpdate,
  onMakeDefault,
  onRemove,
}: {
  name: string
  backend: BackendConfigDto
  isDefault: boolean
  canRemove: boolean
  onRename: (next: string) => void
  onUpdate: (patch: Partial<BackendConfigDto>) => void
  onMakeDefault: () => void
  onRemove: () => void
}) {
  const [nameDraft, setNameDraft] = useState(name)
  useEffect(() => setNameDraft(name), [name])

  return (
    <div className="flex flex-wrap items-center gap-2 rounded-md border border-[var(--border)] p-2">
      <input
        type="text"
        value={nameDraft}
        onChange={(e) => setNameDraft(e.target.value)}
        onBlur={() => onRename(nameDraft.trim())}
        className={`${INPUT} w-28`}
        placeholder="name"
      />
      <input
        type="text"
        value={backend.baseUrl}
        onChange={(e) => onUpdate({ baseUrl: e.target.value })}
        className={`${INPUT} min-w-[180px] flex-1`}
        placeholder="http://localhost:11434"
      />
      <select value={backend.type} onChange={(e) => onUpdate({ type: e.target.value })} className={INPUT}>
        {['auto', 'ollama', 'openai', 'anthropic'].map((t) => (
          <option key={t} value={t}>
            {t}
          </option>
        ))}
      </select>
      <label className="flex items-center gap-1 text-xs text-[var(--muted)]">
        <input
          type="checkbox"
          checked={backend.injectStreamUsage ?? false}
          onChange={(e) => onUpdate({ injectStreamUsage: e.target.checked })}
        />
        injectStreamUsage
      </label>
      <label className="ml-auto flex items-center gap-1 text-xs text-[var(--muted)]">
        <input type="radio" name="default-backend" checked={isDefault} onChange={onMakeDefault} />
        default
      </label>
      <Button variant="ghost" size="sm" disabled={!canRemove} onClick={onRemove}>
        Remove
      </Button>
    </div>
  )
}

function NumberField({ label, value, onChange }: { label: string; value: number; onChange: (v: number) => void }) {
  return (
    <label className="flex flex-col gap-1">
      <span className="text-xs text-[var(--muted)]">{label}</span>
      <input
        type="number"
        value={value}
        onChange={(e) => onChange(Number(e.target.value))}
        className={INPUT}
      />
    </label>
  )
}
