import { useState, type ReactNode } from 'react'
import ReactMarkdown from 'react-markdown'
import remarkGfm from 'remark-gfm'
import type { RenderBlock, RenderedView, RenderMessage } from '@/render'
import { Badge } from '@/components/ui/badge'
import { ToolCallCard } from '@/components/ToolCallCard'

const CLAMP_LENGTH = 4000

/** D4 — renders a normalized `RenderedView`: system block, per-message blocks, params. */
export function MessageView({ view }: { view: RenderedView }) {
  return (
    <div className="flex flex-col gap-3 p-3">
      {view.system && (
        <Card role="system">
          <ClampedMarkdown text={view.system} />
        </Card>
      )}

      {view.messages.map((message, i) => (
        <MessageCard key={i} message={message} />
      ))}

      {view.params.length > 0 && (
        <details className="rounded-control border border-border bg-surface-2 p-2 text-xs">
          <summary className="cursor-pointer select-none font-medium text-text-secondary">Params</summary>
          <div className="mt-2 flex flex-col gap-2">
            {view.params.map((p) => (
              <div key={p.k}>
                <div className="font-medium text-text">{p.k}</div>
                <pre className="mt-1 overflow-x-auto whitespace-pre-wrap break-words font-mono text-text-muted">{p.v}</pre>
              </div>
            ))}
          </div>
        </details>
      )}
    </div>
  )
}

function MessageCard({ message }: { message: RenderMessage }) {
  return (
    <Card role={message.role}>
      {message.blocks.length === 0 ? (
        <span className="text-xs text-text-muted">(empty)</span>
      ) : (
        <div className="flex flex-col gap-2">
          {message.blocks.map((block, i) => (
            <Block key={i} block={block} />
          ))}
        </div>
      )}
    </Card>
  )
}

function Card({ role, children }: { role: string; children: ReactNode }) {
  return (
    <div className="rounded-control border border-border p-2.5">
      <div className="mb-1.5 text-xs font-[550] uppercase tracking-[0.06em] text-text-muted">{role}</div>
      {children}
    </div>
  )
}

function Block({ block }: { block: RenderBlock }) {
  switch (block.kind) {
    case 'markdown':
      return <ClampedMarkdown text={block.text} />
    case 'text':
      return <pre className="whitespace-pre-wrap break-words font-mono text-base text-text">{block.text}</pre>
    case 'thinking':
      return (
        <details className="rounded-control border border-border bg-surface-2 p-2 text-xs">
          <summary className="cursor-pointer select-none text-text-muted">Thinking</summary>
          <div className="mt-2">
            <ClampedMarkdown text={block.text} />
          </div>
        </details>
      )
    case 'image':
      return <Badge variant="neutral">🖼 {block.label}</Badge>
    case 'toolUse':
      return <ToolCallCard kind="use" id={block.id} name={block.name} content={block.argsJson} />
    case 'toolResult':
      return <ToolCallCard kind="result" id={block.forId} content={block.content} />
  }
}

/** Text blocks over ~4000 chars render clamped with an expand control. */
function ClampedMarkdown({ text }: { text: string }) {
  const [expanded, setExpanded] = useState(false)
  const isLong = text.length > CLAMP_LENGTH
  const shown = expanded || !isLong ? text : text.slice(0, CLAMP_LENGTH) + '…'

  return (
    <div className="text-base text-text">
      <div className="md">
        <ReactMarkdown remarkPlugins={[remarkGfm]}>{shown}</ReactMarkdown>
      </div>
      {isLong && (
        <button
          type="button"
          onClick={() => setExpanded((e) => !e)}
          className="mt-1 text-xs text-accent hover:underline"
        >
          {expanded ? 'Show less' : `Show more (${text.length.toLocaleString()} chars)`}
        </button>
      )}
    </div>
  )
}
