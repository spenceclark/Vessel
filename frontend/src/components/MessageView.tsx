import { useState, type ComponentProps, type ReactNode } from 'react'
import ReactMarkdown from 'react-markdown'
import remarkGfm from 'remark-gfm'
import type { ImageSource, RenderBlock, RenderedView, RenderMessage } from '@/render'
import { tryPrettyJson } from '@/render/prettyJson'
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
    case 'markdown': {
      const pretty = tryPrettyJson(block.text)
      return pretty !== null ? <JsonBlock text={pretty} /> : <ClampedMarkdown text={block.text} />
    }
    case 'text': {
      const pretty = tryPrettyJson(block.text)
      if (pretty !== null) return <JsonBlock text={pretty} />
      return <pre className="whitespace-pre-wrap break-words font-mono text-base text-text">{block.text}</pre>
    }
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
      return <ImageBlock label={block.label} source={block.source} />
    case 'toolUse':
      return <ToolCallCard kind="use" id={block.id} name={block.name} content={block.argsJson} />
    case 'toolResult':
      return <ToolCallCard kind="result" id={block.forId} content={block.content} />
  }
}

/**
 * R03/R18 — the placeholder chip is always what's shown first, regardless of source: a
 * click previews **embedded data only**, rendered from the same-document `data:` URI
 * already built by extraction (never a fetch). A URL-sourced image shows the URL as
 * copyable text instead — never fetched, local or remote, matching the same policy
 * markdown images get below.
 */
function ImageBlock({ label, source }: { label: string; source: ImageSource }) {
  const [open, setOpen] = useState(false)
  const previewable = source.kind !== 'unknown'

  // A markdown `![]()` places this inside a <p> (react-markdown wraps the image renderer
  // in one, per its own paragraph handling) — <p> only permits phrasing content, so every
  // element here has to be a <span>/inline element, not a <div>, or the browser force-closes
  // the <p> around it (a real DOM nesting violation, not just a lint nit). `block`/
  // `inline-block` utility classes keep the visual layout identical either way.
  return (
    <span className="inline-block">
      <button
        type="button"
        onClick={() => previewable && setOpen((o) => !o)}
        disabled={!previewable}
        className="disabled:cursor-default"
      >
        <Badge variant="neutral">
          🖼 {label}
          {source.kind === 'url' && ' (remote — not fetched)'}
          {previewable && ` · ${open ? 'hide' : 'preview'}`}
        </Badge>
      </button>
      {open && source.kind === 'embedded' && (
        <img
          src={source.dataUri}
          alt={label}
          className="mt-1.5 block max-h-80 max-w-full rounded-control border border-border"
        />
      )}
      {open && source.kind === 'url' && (
        <span className="mt-1.5 block break-all rounded-control border border-border bg-surface-2 p-2 font-mono text-xs text-text-muted">
          {source.url}
        </span>
      )}
    </span>
  )
}

// R03 — captured content is untrusted, and the viewer's own privacy promise is that
// looking at a capture never makes a network request of its own. `urlTransform` set to
// identity (rather than react-markdown's default sanitizer, which would strip `data:`
// URIs) is deliberate: the actual enforcement is the `img`/`a` overrides below, which
// never emit a live `src`/`href` pointing anywhere but a same-document `data:` URI —
// letting the raw URL value through to them is what lets a data: URI still render while
// every other URL renders as inert text instead of a fetchable resource or a navigable
// link.
const identityUrlTransform = (url: string) => url

function MarkdownImage({ src, alt }: ComponentProps<'img'>) {
  if (typeof src === 'string' && src.startsWith('data:')) {
    return <img src={src} alt={alt} className="max-h-80 max-w-full rounded-control border border-border" />
  }

  return <ImageBlock label={alt || 'image'} source={typeof src === 'string' && src ? { kind: 'url', url: src } : { kind: 'unknown' }} />
}

/** Links render as non-navigating copyable text — no `href`, so there's nothing to click into. */
function MarkdownLink({ href, children }: ComponentProps<'a'>) {
  return (
    <span className="underline decoration-dotted decoration-text-muted" title={href}>
      {children}
    </span>
  )
}

/** §6 code/JSON block look: surface-2, border, radius-control, mono sm, internal scroll. */
function JsonBlock({ text }: { text: string }) {
  return (
    <pre className="max-h-[60vh] overflow-auto whitespace-pre-wrap break-words rounded-control border border-border bg-surface-2 p-3 font-mono text-sm text-text">
      {text}
    </pre>
  )
}

/** Text blocks over ~4000 chars render clamped with an expand control. */
function ClampedMarkdown({ text }: { text: string }) {
  const [expanded, setExpanded] = useState(false)
  const isLong = text.length > CLAMP_LENGTH
  const shown = expanded || !isLong ? text : text.slice(0, CLAMP_LENGTH) + '…'

  return (
    <div className="text-base text-text">
      <div className="md">
        <ReactMarkdown
          remarkPlugins={[remarkGfm]}
          urlTransform={identityUrlTransform}
          components={{ img: MarkdownImage, a: MarkdownLink }}
        >
          {shown}
        </ReactMarkdown>
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
