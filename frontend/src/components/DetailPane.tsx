import { useEffect, useMemo, useState, type ReactNode } from 'react'
import { useQuery } from '@tanstack/react-query'
import { api } from '@/api/client'
import { requestDetailQueryKey } from '@/api/queryKeys'
import type { HeaderMap } from '@/api/types'
import { Badge } from '@/components/ui/badge'
import { Mark } from '@/components/ui/Mark'
import { PrettyJson } from '@/components/PrettyJson'
import { MessageView } from '@/components/MessageView'
import { RenderErrorBoundary } from '@/components/RenderErrorBoundary'
import { ErrorState } from '@/components/ui/ErrorState'
import { renderRequest, renderResponse } from '@/render'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { formatMs, formatTimestamp, formatTokPerSec, formatTokenCount } from '@/lib/format'
import { warningLabel, warningVariant } from '@/lib/warnings'
import { tagVariant } from '@/lib/tags'
import { cn } from '@/lib/utils'

type TabKey = 'overview' | 'request' | 'response' | 'headers'
type ViewMode = 'rendered' | 'raw'

// Stop reasons that indicate something went wrong, distinct from a normal completion
// or the already-flagged truncation (length/max_tokens, shown via "Truncated" above):
// OpenAI's content filter, Anthropic's refusal.
const ERROR_STOP_REASONS = new Set(['content_filter', 'refusal', 'error'])

function isErrorStopReason(reason: string | null): boolean {
  return reason != null && ERROR_STOP_REASONS.has(reason)
}

/**
 * §5 — the detail panel: tab strip as the panel header, content scrolls. Request and
 * Response each default to the rendered message view (D4) when extraction succeeds, with
 * a toggle back to the raw-JSON view — kept exactly as-is, on every tab, regardless of
 * format.
 */
export function DetailPane({ id }: { id: number | null }) {
  const [tab, setTab] = useState<TabKey>('overview')
  const [responseView, setResponseView] = useState<'reassembled' | 'raw'>('reassembled')
  const [requestDisplay, setRequestDisplay] = useState<ViewMode>('rendered')
  const [responseDisplay, setResponseDisplay] = useState<ViewMode>('rendered')

  const query = useQuery({
    queryKey: requestDetailQueryKey(id ?? -1),
    queryFn: () => api.getRequest(id as number),
    enabled: id !== null,
  })

  const requestRendered = useMemo(() => (query.data ? renderRequest(query.data) : null), [query.data])
  const responseRendered = useMemo(() => (query.data ? renderResponse(query.data) : null), [query.data])

  useEffect(() => {
    setResponseView('reassembled')
    setRequestDisplay('rendered')
    setResponseDisplay('rendered')
  }, [id])

  if (id === null) {
    return (
      <div className="flex h-full flex-col items-center justify-center gap-2 p-6 text-center">
        <Mark size={28} muted />
        <p className="text-sm text-text-muted">Select a request to see what it sent and what came back.</p>
      </div>
    )
  }

  if (query.isError) {
    return <ErrorState message="Failed to load this request." onRetry={() => query.refetch()} />
  }

  if (query.isLoading || !query.data) {
    return <div className="p-4 text-sm text-text-muted">Loading…</div>
  }

  const detail = query.data
  const isError = detail.error != null || (detail.statusCode ?? 0) >= 400

  return (
    <Tabs value={tab} onValueChange={(v) => setTab(v as TabKey)} className="flex h-full flex-col">
      <div className="flex items-center justify-between gap-2 border-b border-border px-3 py-2">
        <TabsList>
          <TabsTrigger value="overview">Overview</TabsTrigger>
          <TabsTrigger value="request">Request</TabsTrigger>
          <TabsTrigger value="response">Response</TabsTrigger>
          <TabsTrigger value="headers">Headers</TabsTrigger>
        </TabsList>
      </div>

      <div className="min-h-0 flex-1 overflow-y-auto">
        <TabsContent value="overview" className="p-3">
          <OverviewTab detail={detail} isError={isError} />
        </TabsContent>

        <TabsContent value="request">
          {requestRendered && <ViewModeToggle mode={requestDisplay} onChange={setRequestDisplay} />}
          {requestDisplay === 'rendered' && requestRendered ? (
            <RenderErrorBoundary key={id} fallback={<PrettyJson body={detail.requestBody} emptyLabel="No request body" />}>
              <MessageView view={requestRendered} />
            </RenderErrorBoundary>
          ) : (
            <PrettyJson body={detail.requestBody} emptyLabel="No request body" />
          )}
        </TabsContent>

        <TabsContent value="response">
          {responseRendered && <ViewModeToggle mode={responseDisplay} onChange={setResponseDisplay} />}
          {responseDisplay === 'rendered' && responseRendered ? (
            <RenderErrorBoundary key={id} fallback={<PrettyJson body={detail.responseBody} emptyLabel="No response body" />}>
              <MessageView view={responseRendered} />
            </RenderErrorBoundary>
          ) : (
            <>
              {detail.streamed && detail.responseRaw && (
                <div className="flex items-center gap-2 border-b border-border px-2 py-1 text-xs">
                  <span className="text-text-muted">View:</span>
                  <button
                    type="button"
                    onClick={() => setResponseView('reassembled')}
                    className={cn(
                      'rounded-control px-2 py-0.5',
                      responseView === 'reassembled' ? 'bg-surface-2 font-medium text-text' : 'text-text-muted',
                    )}
                  >
                    Reassembled
                  </button>
                  <button
                    type="button"
                    onClick={() => setResponseView('raw')}
                    className={cn(
                      'rounded-control px-2 py-0.5',
                      responseView === 'raw' ? 'bg-surface-2 font-medium text-text' : 'text-text-muted',
                    )}
                  >
                    Raw stream
                  </button>
                </div>
              )}
              <PrettyJson
                body={responseView === 'raw' && detail.streamed ? detail.responseRaw : detail.responseBody}
                emptyLabel="No response body"
              />
            </>
          )}
        </TabsContent>

        <TabsContent value="headers" className="p-3">
          <div className="flex flex-col gap-6">
            <HeaderTable title="Request headers" headers={detail.requestHeaders} />
            <HeaderTable title="Response headers" headers={detail.responseHeaders} />
          </div>
        </TabsContent>
      </div>
    </Tabs>
  )
}

function OverviewTab({ detail, isError }: { detail: import('@/api/types').RequestDetail; isError: boolean }) {
  return (
    <div className="flex flex-col gap-4 text-sm">
      <div>
        <div className="font-mono font-medium text-text">
          {detail.method} {detail.path}
        </div>
        <div className="text-text-muted">{formatTimestamp(detail.startedAt)}</div>
      </div>

      {detail.warnings.length > 0 && (
        <div className="flex flex-wrap gap-1.5">
          {detail.warnings.map((w) => (
            <Badge key={w} variant={warningVariant(w, isError)}>
              {warningLabel(w)}
            </Badge>
          ))}
        </div>
      )}

      <div>
        <SectionLabel>Request</SectionLabel>
        <CardGrid>
          <MetricCard label="Backend" value={detail.backend} />
          <MetricCard label="Format" value={detail.format} />
          <MetricCard label="Model" value={detail.model ?? '—'} />
          <MetricCard
            label="Status"
            value={detail.error ?? (detail.statusCode != null ? String(detail.statusCode) : '—')}
            danger={isError}
          />
          <MetricCard label="Streamed" value={detail.streamed ? 'yes' : 'no'} />
          <MetricCard label="Truncated" value={detail.truncated ? 'yes' : 'no'} danger={detail.truncated} />
          <MetricCard label="Stop reason" value={detail.stopReason ?? '—'} danger={isErrorStopReason(detail.stopReason)} />
          <MetricCard label="Session" value={detail.sessionId != null ? String(detail.sessionId) : '—'} />
        </CardGrid>
      </div>

      <div>
        <SectionLabel>Timing</SectionLabel>
        <CardGrid>
          <MetricCard label="Duration" value={formatMs(detail.durationMs)} />
          <MetricCard label="TTFT" value={formatMs(detail.ttftMs)} />
          <MetricCard label="Vessel overhead" value={formatMs(detail.vesselOverheadMs)} />
          <MetricCard label="Tok/s" value={formatTokPerSec(detail.tokPerSec)} />
        </CardGrid>
      </div>

      <div>
        <SectionLabel>Tokens</SectionLabel>
        <CardGrid>
          <MetricCard label="In" value={formatTokenCount(detail.tokensIn, detail.tokensEstimated)} />
          <MetricCard label="Out" value={formatTokenCount(detail.tokensOut, detail.tokensEstimated)} />
          <MetricCard label="Cached read" value={formatTokenCount(detail.tokensCachedRead, false)} />
          <MetricCard label="Cached write" value={formatTokenCount(detail.tokensCachedWrite, false)} />
        </CardGrid>
        {detail.format === 'anthropic-messages' && (detail.tokensCachedRead ?? 0) > 0 && (
          <p className="mt-1.5 text-xs text-text-muted">Anthropic's "In" already includes cached tokens.</p>
        )}
      </div>

      <RateLimitCards headers={detail.responseHeaders} />

      {detail.tags.length > 0 && (
        <div>
          <SectionLabel>Tags</SectionLabel>
          <div className="flex flex-wrap gap-1.5">
            {detail.tags.map((t) => (
              <Badge key={t} variant={tagVariant(t)}>
                {t}
              </Badge>
            ))}
          </div>
        </div>
      )}
    </div>
  )
}

const RATE_LIMIT_PREFIX = /^(x-ratelimit-|anthropic-ratelimit-)/i

/**
 * D5/§5.2 — client-side scan of the response headers for `x-ratelimit-*` /
 * `anthropic-ratelimit-*`, grouped by the middle segment (e.g. "requests", "input-tokens")
 * into limit/remaining/reset cards, in the same card-grid language as the rest of
 * Overview. Rendered only when at least one such header exists.
 */
function RateLimitCards({ headers }: { headers: import('@/api/types').HeaderMap | null }) {
  if (!headers) return null

  const groups = new Map<string, { limit?: string; remaining?: string; reset?: string }>()
  for (const [name, values] of Object.entries(headers)) {
    if (!RATE_LIMIT_PREFIX.test(name)) continue
    const rest = name.replace(RATE_LIMIT_PREFIX, '')
    const [kind, ...rest2] = rest.split('-')
    const group = rest2.join('-') || 'default'
    const entry = groups.get(group) ?? {}
    const value = values.join(', ')
    if (kind === 'limit') entry.limit = value
    else if (kind === 'remaining') entry.remaining = value
    else if (kind === 'reset') entry.reset = value
    groups.set(group, entry)
  }

  if (groups.size === 0) return null

  return (
    <div>
      <SectionLabel>Rate limits</SectionLabel>
      <CardGrid>
        {[...groups.entries()].flatMap(([name, row]) => [
          row.limit != null && <MetricCard key={`${name}-limit`} label={`${name} limit`} value={row.limit} />,
          row.remaining != null && (
            <MetricCard key={`${name}-remaining`} label={`${name} remaining`} value={row.remaining} />
          ),
          row.reset != null && <MetricCard key={`${name}-reset`} label={`${name} reset`} value={row.reset} />,
        ])}
      </CardGrid>
    </div>
  )
}

function ViewModeToggle({ mode, onChange }: { mode: ViewMode; onChange: (mode: ViewMode) => void }) {
  return (
    <div className="flex items-center gap-2 border-b border-border px-2 py-1 text-xs">
      <span className="text-text-muted">View:</span>
      <button
        type="button"
        onClick={() => onChange('rendered')}
        className={cn('rounded-control px-2 py-0.5', mode === 'rendered' ? 'bg-surface-2 font-medium text-text' : 'text-text-muted')}
      >
        Rendered
      </button>
      <button
        type="button"
        onClick={() => onChange('raw')}
        className={cn('rounded-control px-2 py-0.5', mode === 'raw' ? 'bg-surface-2 font-medium text-text' : 'text-text-muted')}
      >
        Raw JSON
      </button>
    </div>
  )
}

/** §3 — section labels: xs, uppercase, tracking 0.06em, text-muted, weight 550. Exported for InFlightDetailPane, which reuses the same card-grid look. */
export function SectionLabel({ children }: { children: ReactNode }) {
  return <h3 className="mb-1.5 text-xs font-[550] uppercase tracking-[0.06em] text-text-muted">{children}</h3>
}

/** §5.2 — Overview's metric card grid: 2-3 columns of surface-2, radius-control cards. */
export function CardGrid({ children }: { children: ReactNode }) {
  return <div className="grid grid-cols-2 gap-2 sm:grid-cols-3">{children}</div>
}

export function MetricCard({ label, value, danger }: { label: string; value: string; danger?: boolean }) {
  return (
    <div className="rounded-control bg-surface-2 p-2.5">
      <div className="text-xs font-[550] uppercase tracking-[0.06em] text-text-muted">{label}</div>
      <div className={cn('mt-1 truncate font-mono text-sm', danger ? 'text-danger' : 'text-text')}>{value}</div>
    </div>
  )
}

/** Redacted values (§8) always contain the `…` marker Vessel's own redaction produces. */
function isRedacted(value: string): boolean {
  return value.includes('…')
}

function HeaderTable({ title, headers }: { title: string; headers: HeaderMap | null }) {
  const entries = headers ? Object.entries(headers) : []

  return (
    <div>
      <SectionLabel>{title}</SectionLabel>
      {entries.length === 0 ? (
        <div className="text-sm text-text-muted">None</div>
      ) : (
        <table className="w-full border-collapse text-xs">
          <tbody>
            {entries.map(([name, values]) => {
              const value = values.join(', ')
              return (
                <tr key={name} className="border-b border-border">
                  <td className="w-1/3 py-1 pr-2 align-top font-mono font-medium text-text-muted">{name}</td>
                  <td className="py-1 align-top break-all font-mono">
                    <div className="flex flex-wrap items-center gap-1.5">
                      <span>{value}</span>
                      {isRedacted(value) && <Badge variant="neutral">redacted</Badge>}
                    </div>
                  </td>
                </tr>
              )
            })}
          </tbody>
        </table>
      )}
    </div>
  )
}
