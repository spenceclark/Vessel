import { useState } from 'react'
import type { BodyPayload } from '@/api/types'
import { Button } from '@/components/ui/button'
import { formatBytes } from '@/lib/format'

/**
 * D6 — JSON.parse + JSON.stringify(_, null, 2) in a scrollable <pre>, no syntax
 * highlighting this phase. Unparseable text renders verbatim; base64 bodies show a size
 * placeholder rather than a wall of base64.
 */
export function PrettyJson({
  body,
  emptyLabel = 'No body',
}: {
  body: BodyPayload | null | undefined
  emptyLabel?: string
}) {
  const [collapsed, setCollapsed] = useState(false)
  const [copied, setCopied] = useState(false)

  if (!body) {
    return <div className="p-3 text-sm text-[var(--muted)]">{emptyLabel}</div>
  }

  if (body.base64 !== undefined) {
    const bytes = Math.floor((body.base64.length * 3) / 4)
    return (
      <div className="p-3 text-sm text-[var(--muted)]">
        Binary data (~{formatBytes(bytes)}) — not valid UTF-8, shown as base64 only.
      </div>
    )
  }

  const text = body.text ?? ''
  let pretty = text
  try {
    pretty = JSON.stringify(JSON.parse(text), null, 2)
  } catch {
    // Not JSON — render verbatim.
  }

  async function handleCopy() {
    await navigator.clipboard.writeText(pretty)
    setCopied(true)
    window.setTimeout(() => setCopied(false), 1200)
  }

  return (
    <div className="flex flex-col">
      <div className="flex items-center justify-end gap-2 border-b border-[var(--border)] px-2 py-1">
        <Button variant="ghost" size="sm" onClick={() => setCollapsed((c) => !c)}>
          {collapsed ? 'Expand' : 'Collapse'}
        </Button>
        <Button variant="ghost" size="sm" onClick={handleCopy}>
          {copied ? 'Copied' : 'Copy'}
        </Button>
      </div>
      {!collapsed && (
        <pre className="max-h-[60vh] overflow-auto whitespace-pre-wrap break-words p-3 font-mono text-xs">
          {pretty}
        </pre>
      )}
    </div>
  )
}
