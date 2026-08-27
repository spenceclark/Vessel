import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { api } from '@/api/client'
import type { HeaderMap } from '@/api/types'
import { Badge } from '@/components/ui/badge'
import { PrettyJson } from '@/components/PrettyJson'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { formatMs, formatTimestamp, formatTokPerSec, formatTokenCount } from '@/lib/format'
import { warningLabel } from '@/lib/warnings'
import { cn } from '@/lib/utils'

type TabKey = 'overview' | 'request' | 'response' | 'headers'

/** D6 — the right-hand detail pane: Overview / Request / Response / Headers, raw JSON only this phase. */
export function DetailPane({ id }: { id: number | null }) {
  const [tab, setTab] = useState<TabKey>('overview')
  const [responseView, setResponseView] = useState<'reassembled' | 'raw'>('reassembled')

  const query = useQuery({
    queryKey: ['request', id],
    queryFn: () => api.getRequest(id as number),
    enabled: id !== null,
  })

  if (id === null) {
    return (
      <div className="flex h-full items-center justify-center p-6 text-center text-sm text-[var(--muted)]">
        Select a request to see what it sent and what came back.
      </div>
    )
  }

  if (query.isLoading || !query.data) {
    return <div className="p-4 text-sm text-[var(--muted)]">Loading…</div>
  }

  const detail = query.data
  const isError = detail.error != null || (detail.statusCode ?? 0) >= 400

  return (
    <Tabs value={tab} onValueChange={(v) => setTab(v as TabKey)} className="flex h-full flex-col">
      <div className="flex items-center justify-between gap-2 border-b border-[var(--border)] px-3 pt-2">
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
          <PrettyJson body={detail.requestBody} emptyLabel="No request body" />
        </TabsContent>

        <TabsContent value="response">
          {detail.streamed && detail.responseRaw && (
            <div className="flex items-center gap-2 border-b border-[var(--border)] px-2 py-1 text-xs">
              <span className="text-[var(--muted)]">View:</span>
              <button
                type="button"
                onClick={() => setResponseView('reassembled')}
                className={cn(
                  'rounded px-2 py-0.5',
                  responseView === 'reassembled' ? 'bg-[var(--card)] font-medium' : 'text-[var(--muted)]',
                )}
              >
                Reassembled
              </button>
              <button
                type="button"
                onClick={() => setResponseView('raw')}
                className={cn(
                  'rounded px-2 py-0.5',
                  responseView === 'raw' ? 'bg-[var(--card)] font-medium' : 'text-[var(--muted)]',
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
        <div className="font-medium">
          {detail.method} {detail.path}
        </div>
        <div className="text-[var(--muted)]">{formatTimestamp(detail.startedAt)}</div>
      </div>

      {detail.warnings.length > 0 && (
        <div className="flex flex-wrap gap-1.5">
          {detail.warnings.map((w) => (
            <Badge key={w} variant={isError ? 'danger' : 'warning'}>
              {warningLabel(w)}
            </Badge>
          ))}
        </div>
      )}

      <div className="grid grid-cols-2 gap-x-4 gap-y-2">
        <Field label="Backend" value={detail.backend} />
        <Field label="Format" value={detail.format} />
        <Field label="Model" value={detail.model ?? '—'} />
        <Field
          label="Status"
          value={detail.error ?? (detail.statusCode != null ? String(detail.statusCode) : '—')}
          danger={isError}
        />
        <Field label="Streamed" value={detail.streamed ? 'yes' : 'no'} />
        <Field label="Truncated" value={detail.truncated ? 'yes' : 'no'} danger={detail.truncated} />
        <Field label="Stop reason" value={detail.stopReason ?? '—'} />
        <Field label="Session" value={detail.sessionId != null ? String(detail.sessionId) : '—'} />
      </div>

      <div>
        <h3 className="mb-1 text-xs font-semibold uppercase tracking-wide text-[var(--muted)]">Timing</h3>
        <div className="grid grid-cols-2 gap-x-4 gap-y-2">
          <Field label="Duration" value={formatMs(detail.durationMs)} />
          <Field label="TTFT" value={formatMs(detail.ttftMs)} />
          <Field label="Vessel overhead" value={formatMs(detail.vesselOverheadMs)} />
          <Field label="Tok/s" value={formatTokPerSec(detail.tokPerSec)} />
        </div>
      </div>

      <div>
        <h3 className="mb-1 text-xs font-semibold uppercase tracking-wide text-[var(--muted)]">Tokens</h3>
        <div className="grid grid-cols-2 gap-x-4 gap-y-2">
          <Field label="In" value={formatTokenCount(detail.tokensIn, detail.tokensEstimated)} />
          <Field label="Out" value={formatTokenCount(detail.tokensOut, detail.tokensEstimated)} />
          <Field label="Cached read" value={formatTokenCount(detail.tokensCachedRead, false)} />
          <Field label="Cached write" value={formatTokenCount(detail.tokensCachedWrite, false)} />
        </div>
      </div>

      {detail.tags.length > 0 && (
        <div>
          <h3 className="mb-1 text-xs font-semibold uppercase tracking-wide text-[var(--muted)]">Tags</h3>
          <div className="flex flex-wrap gap-1.5">
            {detail.tags.map((t) => (
              <Badge key={t} variant="outline">
                {t}
              </Badge>
            ))}
          </div>
        </div>
      )}
    </div>
  )
}

function Field({ label, value, danger }: { label: string; value: string; danger?: boolean }) {
  return (
    <div className="flex flex-col">
      <span className="text-xs text-[var(--muted)]">{label}</span>
      <span className={cn('truncate', danger && 'text-[var(--danger)]')}>{value}</span>
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
      <h3 className="mb-1 text-xs font-semibold uppercase tracking-wide text-[var(--muted)]">{title}</h3>
      {entries.length === 0 ? (
        <div className="text-sm text-[var(--muted)]">None</div>
      ) : (
        <table className="w-full border-collapse text-xs">
          <tbody>
            {entries.map(([name, values]) => {
              const value = values.join(', ')
              return (
                <tr key={name} className="border-b border-[var(--border)]">
                  <td className="w-1/3 py-1 pr-2 align-top font-medium text-[var(--muted)]">{name}</td>
                  <td className="py-1 align-top break-all">
                    <div className="flex flex-wrap items-center gap-1.5">
                      <span>{value}</span>
                      {isRedacted(value) && <Badge variant="outline">redacted</Badge>}
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
