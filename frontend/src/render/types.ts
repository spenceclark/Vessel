// D4 — the normalized view model every per-format extractor produces. Rendering is
// entirely client-side from the detail payload; there is no backend-side normalization
// to lean on (each provider's own wire JSON is stored as-is).

export type RenderBlock =
  | { kind: 'markdown'; text: string }
  | { kind: 'text'; text: string }
  | { kind: 'image'; label: string }
  | { kind: 'toolUse'; id?: string; name: string; argsJson: string }
  | { kind: 'toolResult'; forId?: string; content: string }
  | { kind: 'thinking'; text: string }

export interface RenderMessage {
  role: string
  blocks: RenderBlock[]
}

export interface RenderedView {
  system?: string
  messages: RenderMessage[]
  params: { k: string; v: string }[]
}
