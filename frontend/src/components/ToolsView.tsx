import { Fragment, useMemo, useState } from 'react'
import { Search } from 'lucide-react'
import type { RenderedView } from '@/render'
import type { ToolDef, ToolParam } from '@/render/types'
import { countToolCalls } from '@/render/tools'
import { Badge } from '@/components/ui/badge'
import { Input } from '@/components/ui/input'
import { PrettyJson } from '@/components/PrettyJson'
import { cn } from '@/lib/utils'

const FILTER_THRESHOLD = 8

/**
 * #81 — the request's declared tools as cards: name, call count, description, and a
 * parameter table (or config table for server tools), each with its own Rendered/Raw
 * switch. Captured content is untrusted — descriptions render as plain text, never
 * markdown, and nothing becomes a link.
 */
export function ToolsView({
  tools,
  request,
  response,
}: {
  tools: ToolDef[]
  request: RenderedView | null
  response: RenderedView | null
}) {
  const [filter, setFilter] = useState('')
  const callCounts = useMemo(() => countToolCalls([request, response]), [request, response])

  const needle = filter.trim().toLowerCase()
  const shown = tools
    .map((tool, index) => ({ tool, index }))
    .filter(({ tool }) => !needle || tool.name.toLowerCase().includes(needle) || (tool.description?.toLowerCase().includes(needle) ?? false))

  return (
    <div className="flex flex-col gap-3 p-3">
      {tools.length > FILTER_THRESHOLD && (
        <Input icon={<Search />} placeholder="Filter tools" aria-label="Filter tools" value={filter} onChange={(e) => setFilter(e.target.value)} />
      )}
      {shown.map(({ tool, index }) => (
        <ToolCard key={index} tool={tool} calls={callCounts.get(tool.name) ?? 0} />
      ))}
      {shown.length === 0 && <div className="text-sm text-text-muted">No tools match.</div>}
    </div>
  )
}

function ToolCard({ tool, calls }: { tool: ToolDef; calls: number }) {
  const [raw, setRaw] = useState(false)

  return (
    <div className="rounded-control border border-border bg-surface-2">
      <div className="flex flex-wrap items-center gap-2 border-b border-border px-2 py-1.5">
        <span className="break-all font-mono text-sm font-medium text-text">{tool.name}</span>
        {tool.kind === 'server' && <Badge variant="neutral" title={tool.serverType}>server</Badge>}
        <Badge variant={calls > 0 ? 'info' : 'neutral'} className={cn(calls === 0 && 'text-text-muted')}>
          {calls} {calls === 1 ? 'call' : 'calls'}
        </Badge>
        <div className="ml-auto flex items-center gap-1 text-xs">
          <ModeButton active={!raw} onClick={() => setRaw(false)}>Rendered</ModeButton>
          <ModeButton active={raw} onClick={() => setRaw(true)}>Raw</ModeButton>
        </div>
      </div>

      {raw ? (
        <PrettyJson body={{ text: tool.rawJson }} />
      ) : (
        <div className="flex flex-col gap-2 p-2">
          {tool.description && <Description text={tool.description} />}
          {tool.kind === 'server' ? <ConfigTable config={tool.config ?? []} /> : <ParamsTable params={tool.params} />}
        </div>
      )}
    </div>
  )
}

function ModeButton({ active, onClick, children }: { active: boolean; onClick: () => void; children: string }) {
  return (
    <button
      type="button"
      onClick={onClick}
      className={cn('rounded-control px-2 py-0.5', active ? 'bg-surface font-medium text-text' : 'text-text-muted')}
    >
      {children}
    </button>
  )
}

function Description({ text }: { text: string }) {
  const [expanded, setExpanded] = useState(false)
  // ponytail: length/line heuristic for "longer than ~3 lines", measure the DOM if it misjudges.
  const long = text.length > 240 || text.split('\n').length > 3

  return (
    <div className="text-sm text-text-secondary">
      <p className={cn('whitespace-pre-wrap break-words', long && !expanded && 'line-clamp-3')}>{text}</p>
      {long && (
        <button type="button" className="text-xs text-accent hover:underline" onClick={() => setExpanded((e) => !e)}>
          {expanded ? 'Show less' : 'Show more'}
        </button>
      )}
    </div>
  )
}

function ParamsTable({ params }: { params: ToolParam[] }) {
  if (params.length === 0) return <div className="text-xs text-text-muted">No parameters</div>

  return (
    <div className="overflow-x-auto">
      <table className="w-full border-collapse text-xs">
        <thead>
          <tr className="border-b border-border text-left text-text-muted">
            <th className="py-1 pr-2 font-medium">Name</th>
            <th className="py-1 pr-2 font-medium">Type</th>
            <th className="py-1 pr-2 font-medium">Req</th>
            <th className="py-1 pr-2 font-medium">Default</th>
            <th className="py-1 font-medium">Description</th>
          </tr>
        </thead>
        <tbody>
          <ParamRows params={params} depth={0} />
        </tbody>
      </table>
    </div>
  )
}

function ParamRows({ params, depth }: { params: ToolParam[]; depth: number }) {
  return params.map((param, index) => (
    <Fragment key={`${depth}:${index}`}>
      <tr className="border-b border-border align-top">
        <td className="py-1 pr-2 font-mono text-text" style={{ paddingLeft: `${depth * 12}px` }}>{param.name}</td>
        <td className="py-1 pr-2 font-mono text-text-muted">
          {param.type}
          {param.enumValues && (
            <div className="mt-0.5 flex flex-wrap gap-1">
              {param.enumValues.map((value, i) => (
                <span key={i} className="rounded-chip border border-border bg-surface px-1 text-text">{value}</span>
              ))}
            </div>
          )}
        </td>
        <td className="py-1 pr-2 text-text">{param.required ? 'yes' : ''}</td>
        <td className="py-1 pr-2 break-all font-mono text-text">{param.defaultJson ?? ''}</td>
        <td className="py-1 whitespace-pre-wrap break-words text-text-secondary">{param.description ?? ''}</td>
      </tr>
      {param.children && <ParamRows params={param.children} depth={depth + 1} />}
    </Fragment>
  ))
}

function ConfigTable({ config }: { config: { k: string; v: string }[] }) {
  if (config.length === 0) return <div className="text-xs text-text-muted">No configuration</div>

  return (
    <table className="w-full border-collapse text-xs">
      <tbody>
        {config.map(({ k, v }) => (
          <tr key={k} className="border-b border-border">
            <td className="w-1/3 py-1 pr-2 align-top font-mono font-medium text-text-muted">{k}</td>
            <td className="py-1 align-top break-all font-mono text-text">{v}</td>
          </tr>
        ))}
      </tbody>
    </table>
  )
}
