import { useMemo } from 'react'
import { useQueries } from '@tanstack/react-query'
import { api } from '@/api/client'
import { requestDetailQueryKey } from '@/api/queryKeys'
import type { RequestDetail } from '@/api/types'
import { ErrorState } from '@/components/ui/ErrorState'
import { Button } from '@/components/ui/button'
import { MessageView } from '@/components/MessageView'
import { PrettyJson } from '@/components/PrettyJson'
import { renderRequest, renderResponse } from '@/render'
import { formatMs, formatTokPerSec, formatTokenCount } from '@/lib/format'

/** A side-by-side comparison is only meaningful for an original and one of its direct replays. */
export function CompareView({ originalId, replayId, onClose }: { originalId: number; replayId: number; onClose: () => void }) {
  const [originalQuery, replayQuery] = useQueries({
    queries: [
      { queryKey: requestDetailQueryKey(originalId), queryFn: () => api.getRequest(originalId) },
      { queryKey: requestDetailQueryKey(replayId), queryFn: () => api.getRequest(replayId) },
    ],
  })

  if (originalQuery.isLoading || replayQuery.isLoading) return <div className="p-4 text-sm text-text-muted">Loading comparison…</div>
  if (originalQuery.isError || replayQuery.isError || !originalQuery.data || !replayQuery.data) {
    return <ErrorState message="Failed to load this replay pair." onRetry={() => { void originalQuery.refetch(); void replayQuery.refetch() }} />
  }

  const original = originalQuery.data
  const replay = replayQuery.data
  if (replay.replayOf !== original.id) {
    return <ErrorState message="These requests are not a direct replay pair." onRetry={onClose} />
  }

  return <CompareBody original={original} replay={replay} onClose={onClose} />
}

function CompareBody({ original, replay, onClose }: { original: RequestDetail; replay: RequestDetail; onClose: () => void }) {
  const params = useMemo(() => parameterDiff(original, replay), [original, replay])
  const requestView = useMemo(() => renderRequest(original), [original])
  const originalView = useMemo(() => renderResponse(original), [original])
  const replayView = useMemo(() => renderResponse(replay), [replay])
  const metrics: { label: string; a: string; b: string; delta: number | null; formatDelta: (value: number | null) => string }[] = [
    metric('Duration', original.durationMs, replay.durationMs, formatMs),
    metric('TTFT', original.ttftMs, replay.ttftMs, formatMs),
    metric('Tok/s', original.tokPerSec, replay.tokPerSec, formatTokPerSec),
    metric('Tokens in', original.tokensIn, replay.tokensIn, (v) => formatTokenCount(v, false)),
    metric('Tokens out', original.tokensOut, replay.tokensOut, (v) => formatTokenCount(v, false)),
  ]

  return (
    <div className="flex h-full flex-col">
      <div className="flex items-center justify-between border-b border-border px-3 py-2">
        <div>
          <div className="font-mono text-sm text-text">#{original.id} → #{replay.id}</div>
          <div className="text-xs text-text-muted">{original.backend} / {original.model ?? '—'} → {replay.backend} / {replay.model ?? '—'}</div>
        </div>
        <Button variant="ghost" onClick={onClose}>Close</Button>
      </div>
      <div className="min-h-0 flex-1 overflow-y-auto p-3">
        <section>
          <h3 className="mb-2 text-xs font-[550] uppercase tracking-[0.06em] text-text-muted">Metrics</h3>
          <div className="grid grid-cols-2 gap-2 lg:grid-cols-3">
            {metrics.map((item) => <MetricDelta key={item.label} {...item} />)}
            <MetricDelta label="Stop reason" a={original.stopReason ?? '—'} b={replay.stopReason ?? '—'} delta={null} />
          </div>
        </section>
        <RequestPanel detail={original} view={requestView} params={params} />
        <section className="mt-5 grid gap-3 lg:grid-cols-2">
          <ResponsePanel title={`Original #${original.id}`} detail={original} view={originalView} />
          <ResponsePanel title={`Replay #${replay.id}`} detail={replay} view={replayView} />
        </section>
      </div>
    </div>
  )
}

function RequestPanel({ detail, view, params }: {
  detail: RequestDetail
  view: ReturnType<typeof renderRequest>
  params: { name: string; before: string; after: string }[]
}) {
  return (
    <section className="mt-5 rounded-control border border-border">
      <div className="border-b border-border px-3 py-2">
        <h3 className="text-xs font-[550] uppercase tracking-[0.06em] text-text-muted">Request</h3>
        {params.length === 0 ? <p className="mt-1 text-sm text-text-muted">No differing top-level parameters.</p> : (
          <dl className="mt-1 space-y-1 font-mono text-xs">
            {params.map((param) => <div key={param.name} className="grid grid-cols-[minmax(80px,auto)_1fr] gap-2"><dt className="text-text-muted">{param.name}</dt><dd>{param.before} <span className="text-text-muted">→</span> {param.after}</dd></div>)}
          </dl>
        )}
      </div>
      {view ? <MessageView view={view} /> : <PrettyJson body={detail.requestBody} emptyLabel="No request body" />}
    </section>
  )
}

function ResponsePanel({ title, detail, view }: { title: string; detail: RequestDetail; view: ReturnType<typeof renderResponse> }) {
  return <div className="min-w-0 rounded-control border border-border"><h3 className="border-b border-border px-3 py-2 font-mono text-sm text-text">{title}</h3>{view ? <MessageView view={view} /> : <PrettyJson body={detail.responseBody} emptyLabel="No response body" />}</div>
}

export function MetricDelta({ label, a, b, delta, formatDelta }: { label: string; a: string; b: string; delta: number | null; formatDelta?: (value: number | null) => string }) {
  const sign = delta == null || delta === 0
    ? '—'
    : `${delta > 0 ? '+' : '−'}${formatDelta ? formatDelta(Math.abs(delta)) : Math.round(Math.abs(delta) * 10) / 10}`
  return <div className="rounded-control bg-surface-2 p-2.5"><div className="text-xs font-[550] uppercase tracking-[0.06em] text-text-muted">{label}</div><div className="mt-1 font-mono text-sm">{a} <span className="text-text-muted">→</span> {b}</div><div className="mt-1 font-mono text-xs text-text-muted">Δ {sign}</div></div>
}

function metric(label: string, before: number | null, after: number | null, format: (value: number | null) => string) {
  return { label, a: format(before), b: format(after), delta: before == null || after == null ? null : after - before, formatDelta: format }
}

function parameterDiff(original: RequestDetail, replay: RequestDetail): { name: string; before: string; after: string }[] {
  const a = parseObject(original.requestBody?.text)
  const b = parseObject(replay.requestBody?.text)
  if (!a || !b) return []
  const names = new Set([...Object.keys(a), ...Object.keys(b)])
  return [...names].sort().flatMap((name) => {
    const before = JSON.stringify(a[name])
    const after = JSON.stringify(b[name])
    return before === after ? [] : [{ name, before: before ?? 'undefined', after: after ?? 'undefined' }]
  })
}

function parseObject(text?: string): Record<string, unknown> | null {
  if (!text) return null
  try {
    const value: unknown = JSON.parse(text)
    return typeof value === 'object' && value !== null && !Array.isArray(value) ? value as Record<string, unknown> : null
  } catch { return null }
}
