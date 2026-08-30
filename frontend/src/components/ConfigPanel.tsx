import { useEffect, useState, type ReactNode } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { api, ApiError } from '@/api/client'
import type { BackendConfigDto, VesselConfigDto } from '@/api/types'
import { Button } from '@/components/ui/button'
import { ErrorState } from '@/components/ui/ErrorState'
import { Input } from '@/components/ui/input'
import { cn } from '@/lib/utils'

const SELECT_CLASS = 'h-7 rounded-control border border-border bg-surface-2 px-2 text-sm text-text'

interface BackendCatalogEntry {
  key: string
  label: string
  name: string
  baseUrl: string
  type: string
  authEnv?: string
}

// Known backends per docs/architecture.md §9 — keep this catalog in sync with that table.
// It's the single source the "Add backend" picker prefills name/baseUrl/type/authEnv from,
// so the dropdown and the docs can't drift apart.
const BACKEND_CATALOG: readonly BackendCatalogEntry[] = [
  { key: 'ollama', label: 'Ollama', name: 'ollama', baseUrl: 'http://localhost:11434', type: 'ollama' },
  { key: 'lmstudio', label: 'LM Studio', name: 'lmstudio', baseUrl: 'http://localhost:1234', type: 'openai' },
  { key: 'llamacpp', label: 'llama.cpp', name: 'llamacpp', baseUrl: 'http://localhost:8080', type: 'openai' },
  { key: 'vllm', label: 'vLLM', name: 'vllm', baseUrl: 'http://localhost:8000', type: 'openai' },
  { key: 'lemonade', label: 'Lemonade', name: 'lemonade', baseUrl: 'http://localhost:13305', type: 'openai' },
  { key: 'unsloth', label: 'Unsloth', name: 'unsloth', baseUrl: 'http://localhost:8888', type: 'openai' },
  {
    key: 'openai',
    label: 'OpenAI',
    name: 'openai',
    baseUrl: 'https://api.openai.com',
    type: 'openai',
    authEnv: 'OPENAI_API_KEY',
  },
  {
    key: 'anthropic',
    label: 'Anthropic / Claude',
    name: 'anthropic',
    baseUrl: 'https://api.anthropic.com',
    type: 'anthropic',
    authEnv: 'ANTHROPIC_API_KEY',
  },
  {
    key: 'gemini',
    label: 'Gemini',
    name: 'gemini',
    baseUrl: 'https://generativelanguage.googleapis.com/v1beta/openai',
    type: 'openai',
    authEnv: 'GEMINI_API_KEY',
  },
]

const CUSTOM_BACKEND_KEY = 'custom'

/**
 * D7 — the config editor: backends table (add from a known-backend catalog/remove/default/
 * exact-token-counts toggle), retention/capture/slow-TTFT numbers, listen (flagged as
 * restart-required). Client-side required-field checks before Save; the server's 400
 * validation message surfaces verbatim on failure.
 */
export function ConfigPanel() {
  const queryClient = useQueryClient()
  const configQuery = useQuery({ queryKey: ['config'], queryFn: api.getConfig })
  const [draft, setDraft] = useState<VesselConfigDto | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)
  // R16: a just-saved PUT's answer is the freshest signal; once the post-save refetch of
  // ['config'] resolves, GET's persisted state (survives reopen, unlike component state)
  // takes back over. Derived at render time rather than mirrored into state via an effect.
  const [justSavedRestartRequired, setJustSavedRestartRequired] = useState<string[] | null>(null)
  const restartRequired = justSavedRestartRequired ?? configQuery.data?.restartRequired ?? []
  const [renameError, setRenameError] = useState<{ backend: string; message: string } | null>(null)

  useEffect(() => {
    if (configQuery.data && draft === null) {
      // oxlint-disable-next-line react/set-state-in-effect -- initializes the editable draft once from the async persisted config.
      setDraft(configQuery.data.config)
    }
  }, [configQuery.data, draft])

  if (configQuery.isError && !draft) {
    return <ErrorState message="Failed to load config." onRetry={() => configQuery.refetch()} />
  }

  if (!draft) {
    return <div className="text-sm text-text-muted">Loading…</div>
  }

  function updateBackend(name: string, patch: Partial<BackendConfigDto>) {
    setDraft((d) => (d ? { ...d, backends: { ...d.backends, [name]: { ...d.backends[name], ...patch } } } : d))
  }

  // R13 — `renameBackend` used to delete-then-assign with no collision check, so renaming
  // onto an existing name silently overwrote it: two backend rows collapsed into one, and
  // the server can't catch it because only one property survives in the submitted JSON.
  // The server's own name comparison is case-insensitive (D7), so the guard here matches
  // that — while still allowing a case-only rename of the *same* backend (that's not a
  // collision, it's the rename being performed).
  function renameBackend(oldName: string, newName: string) {
    if (!newName || newName === oldName) return

    const collision = Object.keys(draft?.backends ?? {}).some(
      (existing) => existing !== oldName && existing.toLowerCase() === newName.toLowerCase(),
    )
    if (collision) {
      setRenameError({ backend: oldName, message: `A backend named "${newName}" already exists.` })
      return
    }

    setRenameError((e) => (e?.backend === oldName ? null : e))
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
    setRenameError((e) => (e?.backend === name ? null : e))
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

  function addBackend(entry?: BackendCatalogEntry) {
    setDraft((d) => {
      if (!d) return d
      const base = entry?.name ?? 'new-backend'
      let name = base
      let n = 2
      while (name in d.backends) name = `${base}-${n++}`
      const backend: BackendConfigDto = entry
        ? { baseUrl: entry.baseUrl, type: entry.type, ...(entry.authEnv ? { authEnv: entry.authEnv } : {}) }
        : { baseUrl: '', type: 'auto' }
      return { ...d, backends: { ...d.backends, [name]: backend } }
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
      setJustSavedRestartRequired(result.restartRequired)
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: ['status'] }),
        queryClient.invalidateQueries({ queryKey: ['facets'] }),
        queryClient.invalidateQueries({ queryKey: ['config'] }),
      ])
      // ['config'] has now refetched (invalidateQueries awaits active refetches), so
      // configQuery.data already carries this same answer — stop shadowing it.
      setJustSavedRestartRequired(null)
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Failed to save config.')
    } finally {
      setSaving(false)
    }
  }

  return (
    <div className="flex flex-col gap-4 text-sm">
      {restartRequired.length > 0 && (
        <div className="rounded-control border border-[color-mix(in_srgb,var(--color-warn)_40%,transparent)] bg-[color-mix(in_srgb,var(--color-warn)_10%,transparent)] px-3 py-2 text-xs text-warn">
          Pending restart: {restartRequired.join(', ')} change{restartRequired.length === 1 ? 's' : ''} will apply on next start.
        </div>
      )}
      {error && (
        <div className="rounded-control border border-[color-mix(in_srgb,var(--color-danger)_40%,transparent)] bg-[color-mix(in_srgb,var(--color-danger)_10%,transparent)] px-3 py-2 text-xs text-danger">
          {error}
        </div>
      )}

      <div>
        <div className="mb-1.5 flex items-center justify-between">
          <SectionLabel>Backends</SectionLabel>
          <select
            value=""
            onChange={(e) => {
              const key = e.target.value
              if (!key) return
              if (key === CUSTOM_BACKEND_KEY) {
                addBackend()
                return
              }
              const entry = BACKEND_CATALOG.find((b) => b.key === key)
              if (entry) addBackend(entry)
            }}
            className={SELECT_CLASS}
            aria-label="Add backend"
          >
            <option value="" disabled>
              Add backend…
            </option>
            {BACKEND_CATALOG.map((b) => (
              <option key={b.key} value={b.key}>
                {b.label}
              </option>
            ))}
            <option value={CUSTOM_BACKEND_KEY}>Custom…</option>
          </select>
        </div>
        <div className="flex flex-col gap-2">
          {Object.entries(draft.backends).map(([name, backend]) => (
            <BackendRow
              key={name}
              name={name}
              backend={backend}
              isDefault={draft.defaultBackend === name}
              canRemove={Object.keys(draft.backends).length > 1}
              renameError={renameError?.backend === name ? renameError.message : null}
              onRename={(next) => renameBackend(name, next)}
              onUpdate={(patch) => updateBackend(name, patch)}
              onMakeDefault={() => setDraft((d) => (d ? { ...d, defaultBackend: name } : d))}
              onRemove={() => removeBackend(name)}
            />
          ))}
        </div>
      </div>

      <div>
        <SectionLabel>Retention & capture</SectionLabel>
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
        <SectionLabel>Listen address</SectionLabel>
        <Input
          type="text"
          value={draft.listen}
          onChange={(e) => setDraft((d) => (d ? { ...d, listen: e.target.value } : d))}
          className="w-48 font-mono"
        />
        <p className="mt-1 text-xs text-text-muted">Changes on next start — not applied live.</p>
      </div>

      <div>
        <SectionLabel>MCP access</SectionLabel>
        <label className="flex items-center gap-2 text-sm text-text">
          <input
            type="checkbox"
            checked={draft.mcp.enabled}
            onChange={(e) => setDraft((d) => (d ? { ...d, mcp: { enabled: e.target.checked } } : d))}
          />
          Enable MCP server
        </label>
        <p className="mt-1 text-xs text-text-muted">An MCP client you connect can read captured prompts.</p>
      </div>

      <div>
        <Button variant="primary" onClick={handleSave} disabled={saving}>
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
  renameError,
  onRename,
  onUpdate,
  onMakeDefault,
  onRemove,
}: {
  name: string
  backend: BackendConfigDto
  isDefault: boolean
  canRemove: boolean
  renameError: string | null
  onRename: (next: string) => void
  onUpdate: (patch: Partial<BackendConfigDto>) => void
  onMakeDefault: () => void
  onRemove: () => void
}) {
  const [nameDraft, setNameDraft] = useState(name)
  // oxlint-disable-next-line react/set-state-in-effect -- a backend rename from the parent must replace this row's local text draft.
  useEffect(() => setNameDraft(name), [name])

  return (
    <div className="flex flex-col gap-1 rounded-control border border-border p-2">
      <div className="flex flex-wrap items-center gap-2">
        <Input
          type="text"
          value={nameDraft}
          onChange={(e) => setNameDraft(e.target.value)}
          onBlur={() => onRename(nameDraft.trim())}
          className={cn('w-28 font-mono', renameError && 'border-danger')}
          placeholder="name"
        />
        <Input
          type="text"
          value={backend.baseUrl}
          onChange={(e) => onUpdate({ baseUrl: e.target.value })}
          className="min-w-[180px] flex-1 font-mono"
          placeholder="http://localhost:11434"
        />
        <div className="flex flex-col gap-1">
          <select value={backend.type} onChange={(e) => onUpdate({ type: e.target.value })} className={SELECT_CLASS}>
            {['auto', 'ollama', 'openai', 'anthropic'].map((t) => (
              <option key={t} value={t}>
                {t}
              </option>
            ))}
          </select>
          <p className="text-xs text-text-muted">auto = detect from traffic; observation only — typed backends unlock replay targeting and correct replay auth.</p>
        </div>
        {(backend.type === 'openai' || backend.type === 'auto') && (
          <div className="flex flex-col gap-1">
            <label className="flex items-center gap-1 text-xs text-text">
              <input
                type="checkbox"
                checked={backend.injectStreamUsage ?? false}
                onChange={(e) => onUpdate({ injectStreamUsage: e.target.checked })}
              />
              Exact token counts (streamed)
            </label>
            <p className="text-xs text-text-muted">
              Adds <code className="font-mono">include_usage</code> to streamed OpenAI-format requests so token
              counts are exact instead of estimated (~). Modifies the outgoing request only — the captured bytes
              are still exactly what the client sent.
            </p>
          </div>
        )}
        <Input
          type="text"
          value={backend.authEnv ?? ''}
          onChange={(e) => onUpdate({ authEnv: e.target.value || undefined })}
          className="w-40 font-mono"
          placeholder="auth env (optional)"
          aria-label={`Authentication environment variable for ${name}`}
        />
        <label className="ml-auto flex items-center gap-1 text-xs text-text-muted">
          <input type="radio" name="default-backend" checked={isDefault} onChange={onMakeDefault} />
          default
        </label>
        <Button variant="ghost" disabled={!canRemove} onClick={onRemove}>
          Remove
        </Button>
      </div>
      {renameError && <p className="text-xs text-danger">{renameError}</p>}
    </div>
  )
}

function NumberField({ label, value, onChange }: { label: string; value: number; onChange: (v: number) => void }) {
  return (
    <label className="flex flex-col gap-1">
      <span className="text-xs text-text-muted">{label}</span>
      <Input type="number" value={value} onChange={(e) => onChange(Number(e.target.value))} className="font-mono" />
    </label>
  )
}

function SectionLabel({ children }: { children: ReactNode }) {
  return <h3 className="text-xs font-[550] uppercase tracking-[0.06em] text-text-muted">{children}</h3>
}
