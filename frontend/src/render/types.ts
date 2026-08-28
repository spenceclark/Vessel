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
  | { kind: 'markdown'; text: string }
  | { kind: 'text'; text: string }
  | { kind: 'image'; label: string; source: ImageSource }
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
