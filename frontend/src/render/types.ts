// D4 — the normalized view model every per-format extractor produces. Rendering is
// entirely client-side from the detail payload; there is no backend-side normalization
// to lean on (each provider's own wire JSON is stored as-is).

// R03/R18 — captured content is untrusted, and the viewer's own privacy promise is that
// looking at a capture never makes a network request of its own. An image block therefore
// never carries something a renderer could point an <img> at directly; it carries a
// *source description* the preview interaction (MessageView) decides how to handle:
// embedded bytes render from a same-document data: URI (no request), a URL is shown as
// text only (never fetched, local or remote — R03's synthetic-stub repro was a *local*
// URL).
export type ImageSource =
  | { kind: 'embedded'; dataUri: string }
  | { kind: 'url'; url: string }
  | { kind: 'unknown' }

export type RenderBlock =
  | { kind: 'markdown'; text: string; citations?: Citation[] }
  | { kind: 'text'; text: string; citations?: Citation[] }
  | { kind: 'image'; label: string; source: ImageSource }
  | { kind: 'toolUse'; id?: string; name: string; argsJson: string; server?: boolean }
  | { kind: 'toolResult'; forId?: string; content: string }
  // #82 — Anthropic server-tool results (`web_search_tool_result`, …). `items` is empty for
  // tool types we don't have a shape for; `rawJson` always carries the block.
  | { kind: 'serverToolResult'; forId?: string; toolType: string; error?: string; items: ServerToolItem[]; rawJson: string }
  | { kind: 'thinking'; text: string }

export interface Citation {
  url?: string
  title?: string
  citedText?: string
}

export interface ServerToolItem {
  title?: string
  url?: string
  meta?: string
  snippet?: string
}

export interface RenderMessage {
  role: string
  blocks: RenderBlock[]
}

export interface ToolParam {
  name: string
  type: string // 'string' | 'integer' | 'string | null' | 'object' | 'array<string>' | 'enum' …
  required: boolean
  description?: string
  defaultJson?: string // JSON.stringify(default) when present
  enumValues?: string[]
  children?: ToolParam[] // nested object properties / array item object properties
}

// #81 — one declared tool, normalized across formats. Server tools (Anthropic
// `web_search_20250305`, Responses `web_search_preview`, …) have no schema, only config.
export interface ToolDef {
  name: string
  kind: 'function' | 'server' | 'unknown'
  serverType?: string
  description?: string
  params: ToolParam[]
  config?: { k: string; v: string }[]
  rawJson: string
}

export interface RenderedView {
  system?: string
  messages: RenderMessage[]
  params: { k: string; v: string }[]
  tools?: ToolDef[]
}
