import { useState } from 'react'
import { ChevronDown, ChevronRight, Reply, Wrench } from 'lucide-react'
import { cn } from '@/lib/utils'

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
  const Icon = kind === 'use' ? Wrench : Reply

  return (
    <div className="rounded-control border border-border bg-surface-2">
      <button
        type="button"
        onClick={() => setCollapsed((c) => !c)}
        className="flex w-full items-center gap-2 px-2 py-1.5 text-left text-xs"
      >
        <Icon className="h-3.5 w-3.5 shrink-0 text-accent" strokeWidth={1.75} />
        <span className="font-medium text-text">{kind === 'use' ? (name ?? 'tool call') : 'tool result'}</span>
        {id && <span className="truncate font-mono text-text-muted">#{id}</span>}
        {collapsed ? (
          <ChevronRight className="ml-auto h-3.5 w-3.5 shrink-0 text-text-muted" strokeWidth={1.75} />
        ) : (
          <ChevronDown className="ml-auto h-3.5 w-3.5 shrink-0 text-text-muted" strokeWidth={1.75} />
        )}
      </button>
      {!collapsed && (
        <pre
          className={cn(
            'max-h-64 overflow-auto whitespace-pre-wrap break-words border-t border-border px-2 py-1.5 font-mono text-xs text-text',
          )}
        >
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
