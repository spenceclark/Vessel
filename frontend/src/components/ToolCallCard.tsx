import { useState } from 'react'
import { ChevronDown, ChevronRight, Reply, Wrench } from 'lucide-react'
import { cn } from '@/lib/utils'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { PrettyJson } from '@/components/PrettyJson'
import type { ServerToolItem } from '@/render/types'

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
  server,
}: {
  kind: 'use' | 'result'
  id?: string
  name?: string
  content: string
  server?: boolean
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
        {server && <Badge variant="info">server</Badge>}
        {id && <span className="truncate font-mono text-text-muted">#{id}</span>}
        {collapsed ? (
          <ChevronRight className="ml-auto h-3.5 w-3.5 shrink-0 text-text-muted" strokeWidth={1.75} />
        ) : (
          <ChevronDown className="ml-auto h-3.5 w-3.5 shrink-0 text-text-muted" strokeWidth={1.75} />
        )}
      </button>
      {!collapsed && <ToolContent content={content} />}
    </div>
  )
}

const BODY_CLASS = 'max-h-64 overflow-auto border-t border-border px-2 py-1.5 font-mono text-xs text-text'

/**
 * #85 — a plain JSON object renders as a field list so multi-line strings (`code`,
 * `stdout`, …) show real newlines instead of escaped `\n`. Top level only; anything else
 * (arrays, primitives, unparseable text) stays pretty-printed or verbatim.
 */
function ToolContent({ content }: { content: string }) {
  const fields = objectFields(content)
  if (!fields) {
    return <pre className={cn(BODY_CLASS, 'whitespace-pre-wrap break-words')}>{prettyOrRaw(content)}</pre>
  }

  return (
    <div className={cn(BODY_CLASS, 'flex flex-col gap-1.5')}>
      {fields.map(([key, value]) => {
        // Strings verbatim; an empty string still shows as `""` so it isn't mistaken for missing.
        const text = typeof value === 'string' && value !== '' ? value : JSON.stringify(value, null, 2)
        return text.includes('\n') ? (
          <div key={key}>
            <div className="text-text-muted">{key}</div>
            <pre className="whitespace-pre-wrap break-words pl-3">{text}</pre>
          </div>
        ) : (
          <div key={key} className="flex gap-3">
            <span className="shrink-0 text-text-muted">{key}</span>
            <pre className="min-w-0 whitespace-pre-wrap break-words">{text}</pre>
          </div>
        )
      })}
    </div>
  )
}

function objectFields(content: string): [string, unknown][] | null {
  try {
    const parsed: unknown = JSON.parse(content)
    if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) return null
    const entries = Object.entries(parsed)
    return entries.length > 0 ? entries : null
  } catch {
    return null
  }
}

/**
 * #82 — an Anthropic server-tool result (`web_search`, `web_fetch`, …), collapsed by
 * default since a single search can carry many results. R03/R18: URLs are plain text,
 * never links or fetched.
 */
export function ServerToolResultCard({
  forId,
  toolType,
  error,
  items,
  rawJson,
}: {
  forId?: string
  toolType: string
  error?: string
  items: ServerToolItem[]
  rawJson: string
}) {
  const [collapsed, setCollapsed] = useState(true)
  const [raw, setRaw] = useState(false)

  return (
    <div className="rounded-control border border-border bg-surface-2">
      <button
        type="button"
        onClick={() => setCollapsed((c) => !c)}
        className="flex w-full items-center gap-2 px-2 py-1.5 text-left text-xs"
      >
        <Reply className="h-3.5 w-3.5 shrink-0 text-accent" strokeWidth={1.75} />
        <span className="font-medium text-text">
          {toolType}
          {!error && items.length > 0 && ` · ${items.length} ${items.length === 1 ? 'result' : 'results'}`}
        </span>
        {error && <Badge variant="danger">{error}</Badge>}
        {forId && <span className="truncate font-mono text-text-muted">#{forId}</span>}
        {collapsed ? (
          <ChevronRight className="ml-auto h-3.5 w-3.5 shrink-0 text-text-muted" strokeWidth={1.75} />
        ) : (
          <ChevronDown className="ml-auto h-3.5 w-3.5 shrink-0 text-text-muted" strokeWidth={1.75} />
        )}
      </button>
      {!collapsed && (
        <div className="flex flex-col items-start gap-2 border-t border-border px-2 py-1.5 text-xs">
          {items.length > 0 && (
            <ol className="flex w-full flex-col gap-2">
              {items.map((item, i) => (
                <li key={i} className="flex flex-col gap-0.5">
                  {item.title && <span className="font-medium text-text">{item.title}</span>}
                  {item.url && <span className="break-all font-mono text-text-muted">{item.url}</span>}
                  {item.meta && <span className="text-text-muted">{item.meta}</span>}
                  {item.snippet && (
                    <pre className="max-h-40 overflow-auto whitespace-pre-wrap break-words font-mono text-text-secondary">
                      {item.snippet}
                    </pre>
                  )}
                </li>
              ))}
            </ol>
          )}
          <Button variant="ghost" onClick={() => setRaw((r) => !r)}>
            {raw ? 'Hide raw' : 'Raw'}
          </Button>
          {raw && (
            <div className="w-full">
              <PrettyJson body={{ text: rawJson }} />
            </div>
          )}
        </div>
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
