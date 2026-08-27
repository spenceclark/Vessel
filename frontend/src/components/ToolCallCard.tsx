import { useState } from 'react'

/**
 * D4 — a tool call (use) or its result, collapsible. `kind="use"` and `kind="result"`
 * share styling; a result card is visually linked to its call by carrying the same id in
 * its header when present (matched by the caller via `forId`/`id`).
 */
export function ToolCallCard({
  kind,
  id,
  name,
  content,
}: {
  kind: 'use' | 'result'
  id?: string
  name?: string
  content: string
}) {
  const [collapsed, setCollapsed] = useState(false)

  return (
    <div className="rounded-md border border-[var(--border)] bg-[var(--card)]">
      <button
        type="button"
        onClick={() => setCollapsed((c) => !c)}
        className="flex w-full items-center gap-2 px-2 py-1.5 text-left text-xs"
      >
        <span className="font-mono text-[var(--accent)]">{kind === 'use' ? '🔧' : '↩'}</span>
        <span className="font-medium">{kind === 'use' ? (name ?? 'tool call') : 'tool result'}</span>
        {id && <span className="truncate text-[var(--muted)]">#{id}</span>}
        <span className="ml-auto text-[var(--muted)]">{collapsed ? 'expand' : 'collapse'}</span>
      </button>
      {!collapsed && (
        <pre className="max-h-64 overflow-auto whitespace-pre-wrap break-words border-t border-[var(--border)] px-2 py-1.5 font-mono text-xs">
          {prettyOrRaw(content)}
        </pre>
      )}
    </div>
  )
}

function prettyOrRaw(content: string): string {
  try {
    return JSON.stringify(JSON.parse(content), null, 2)
  } catch {
    return content
  }
}
