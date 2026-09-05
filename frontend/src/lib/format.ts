export function formatMs(ms: number | null | undefined): string {
  if (ms == null || Number.isNaN(ms)) return '—'
  if (ms < 1000) return `${Math.round(ms)}ms`
  return `${(ms / 1000).toFixed(2)}s`
}

export function formatTokPerSec(v: number | null | undefined): string {
  if (v == null) return '—'
  return `${v.toFixed(1)} tok/s`
}

export function formatTokenCount(v: number | null | undefined, estimated: boolean): string {
  if (v == null) return '—'
  const formatted = v.toLocaleString('en-US')
  return estimated ? `~${formatted}` : formatted
}

/**
 * ui-spec.md §9.1 (token-totals) — dense header-stat form: unabbreviated below 10k,
 * one decimal `k`/`M` above (847, 12.4k, 1.2M). Distinct from `formatTokenCount`
 * (thousands-separated, for the detail panel where more width is available).
 */
export function formatCompactTokenCount(v: number, estimated: boolean): string {
  const millions = v / 1_000_000
  const compact = millions >= 0.9995 ? `${millions.toFixed(1)}M` : v < 10_000 ? String(v) : `${(v / 1000).toFixed(1)}k`
  return estimated ? `~${compact}` : compact
}

/** §5.1 — path truncates middle-out (start…end), not end-truncated like CSS text-overflow. */
export function truncateMiddle(s: string, max = 42): string {
  if (s.length <= max) return s
  const keep = max - 1
  const head = Math.ceil(keep * 0.6)
  const tail = keep - head
  return `${s.slice(0, head)}…${s.slice(s.length - tail)}`
}

/**
 * §5.1 — splits an already-formatted metric ("3.04s", "47.0 tok/s") into
 * [digits, unit] so a row's fixed-width metric columns can dim the unit and let the
 * digits carry the emphasis. Values with no leading digits (the "—" placeholder)
 * come back as [formatted, ''] — rendered plain, matching every other em-dash.
 */
export function splitMetric(formatted: string): [string, string] {
  const match = formatted.match(/^(-?[\d,.]+)(.*)$/)
  return match ? [match[1], match[2]] : [formatted, '']
}

export function formatBytes(n: number): string {
  if (n < 1024) return `${n} B`
  if (n < 1024 * 1024) return `${(n / 1024).toFixed(1)} KB`
  return `${(n / (1024 * 1024)).toFixed(1)} MB`
}

/** Coarse "how long ago", for list/summary chrome where an exact timestamp is noise. */
export function relativeDate(iso: string): string {
  const time = new Date(iso).getTime()
  if (!Number.isFinite(time)) return iso
  const seconds = Math.max(0, Math.floor((Date.now() - time) / 1000))
  if (seconds < 60) return 'just now'
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m ago`
  if (seconds < 86_400) return `${Math.floor(seconds / 3600)}h ago`
  if (seconds < 2_592_000) return `${Math.floor(seconds / 86_400)}d ago`
  return new Date(time).toLocaleDateString()
}

export function formatTimestamp(iso: string): string {
  const date = new Date(iso)
  if (Number.isNaN(date.getTime())) return iso
  return date.toLocaleString()
}
