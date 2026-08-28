import { AlertTriangle } from 'lucide-react'
import type { BodyPayload } from '@/api/types'
import { formatBytes } from '@/lib/format'

/**
 * R05 remainder — `BodyPayload.decodeTruncated` means the *display* decode hit
 * `capture.maxBodyMb` and what's shown is a prefix, not the whole body. This is
 * independent of capture-time truncation (the `Truncated` overview card / `body_truncated`
 * warning badge, which means the wire bytes themselves were cut off at capture) — a body
 * can be decode-truncated without ever having been capture-truncated, e.g. after lowering
 * the config cap on an already-fully-captured compressed response. Shown above the body
 * regardless of view mode (rendered, raw JSON, raw stream) since all three read from the
 * same possibly-truncated payload.
 */
export function DecodeTruncatedNotice({ body }: { body: BodyPayload | null | undefined }) {
  if (!body?.decodeTruncated) return null

  const shownBytes =
    body.text !== undefined
      ? new TextEncoder().encode(body.text).length
      : body.base64 !== undefined
        ? Math.floor((body.base64.length * 3) / 4)
        : 0

  return (
    <div
      role="alert"
      className="flex items-center gap-2 border-b border-border bg-[color-mix(in_srgb,var(--color-warn)_10%,transparent)] px-3 py-1.5 text-xs text-warn"
    >
      <AlertTriangle className="h-3.5 w-3.5 shrink-0" strokeWidth={1.75} aria-hidden="true" />
      <span>
        Showing the first {formatBytes(shownBytes)} of this body — display decode limit reached (raise{' '}
        <code className="font-mono">capture.maxBodyMb</code> to see more).
      </span>
    </div>
  )
}
