import type { RenderedView, ToolDef, ToolParam } from './types'

const MAX_DEPTH = 4

/**
 * #81 — the request's declared tool list as structured defs, shared by every renderer so
 * the JSON Schema walk lives in one place. Captured JSON is untrusted: every field is
 * type-checked as it's read (the result only ever holds strings/booleans), and a malformed
 * entry is skipped rather than thrown — the rest of the list still renders.
 */
export function extractTools(format: string, rawTools: unknown): ToolDef[] {
  if (!Array.isArray(rawTools)) return []
  const tools: ToolDef[] = []
  for (const entry of rawTools) {
    try {
      const tool = toolDef(format, entry)
      if (tool) tools.push(tool)
    } catch {
      // Malformed entry — keep what parsed; rawJson of the whole request is still available.
    }
  }
  return tools
}

function toolDef(format: string, entry: unknown): ToolDef | null {
  if (!isRecord(entry)) return null
  const rawJson = JSON.stringify(entry, null, 2)
  const type = str(entry.type)

  if (format === 'anthropic-messages') {
    // Server tools (`web_search_20250305`, `web_fetch_20250910`, …) carry a versioned
    // `type` and config fields instead of an `input_schema`.
    if (type && type !== 'custom' && entry.input_schema === undefined) {
      return server(entry, type, rawJson)
    }
    return function_(entry.name, entry.description, entry.input_schema, rawJson)
  }

  if (format === 'openai-responses') {
    if (type === 'function') return function_(entry.name, entry.description, entry.parameters, rawJson)
    return type ? server(entry, type, rawJson) : unknown(entry, rawJson)
  }

  // openai-chat / ollama-chat: `{ type: 'function', function: { name, description, parameters } }`
  const fn = entry.function
  if (isRecord(fn)) return function_(fn.name, fn.description, fn.parameters, rawJson)
  return unknown(entry, rawJson)
}

function function_(name: unknown, description: unknown, schema: unknown, rawJson: string): ToolDef {
  return { name: str(name) ?? 'tool', kind: 'function', description: str(description), params: schemaToParams(schema), rawJson }
}

function server(entry: Record<string, unknown>, type: string, rawJson: string): ToolDef {
  const config = Object.entries(entry)
    .filter(([k, v]) => k !== 'type' && k !== 'name' && v !== null && v !== undefined)
    .map(([k, v]) => ({ k, v: typeof v === 'string' ? v : JSON.stringify(v) }))
  return { name: str(entry.name) ?? type, kind: 'server', serverType: type, params: [], config, rawJson }
}

function unknown(entry: Record<string, unknown>, rawJson: string): ToolDef {
  return { name: str(entry.name) ?? str(entry.type) ?? 'tool', kind: 'unknown', params: [], rawJson }
}

export function schemaToParams(schema: unknown, depth = 1): ToolParam[] {
  if (!isRecord(schema) || !isRecord(schema.properties)) return []
  const required = Array.isArray(schema.required) ? schema.required : []
  return Object.entries(schema.properties).map(([name, prop]) => toParam(name, prop, required.includes(name), depth))
}

function toParam(name: string, prop: unknown, required: boolean, depth: number): ToolParam {
  const param: ToolParam = { name, type: typeOf(prop), required }
  if (!isRecord(prop)) return param

  param.description = str(prop.description)
  if (prop.default !== undefined) param.defaultJson = JSON.stringify(prop.default)
  if (Array.isArray(prop.enum)) param.enumValues = prop.enum.map((v) => (typeof v === 'string' ? v : JSON.stringify(v)))

  if (depth < MAX_DEPTH) {
    const children = schemaToParams(prop, depth + 1)
    const itemChildren = isRecord(prop.items) ? schemaToParams(prop.items, depth + 1) : []
    if (children.length > 0 || itemChildren.length > 0) param.children = [...children, ...itemChildren]
  }
  return param
}

function typeOf(schema: unknown): string {
  if (!isRecord(schema)) return 'unknown'
  if (typeof schema.type === 'string') {
    return schema.type === 'array' && isRecord(schema.items) ? `array<${typeOf(schema.items)}>` : schema.type
  }
  if (Array.isArray(schema.type)) return joinTypes(schema.type.map((t) => str(t) ?? 'unknown'))
  const union = schema.anyOf ?? schema.oneOf
  if (Array.isArray(union)) return joinTypes(union.map(typeOf))
  if (Array.isArray(schema.enum)) return 'enum'
  return 'unknown'
}

function joinTypes(parts: string[]): string {
  return parts.length === 0 || parts.includes('unknown') ? 'unknown' : parts.join(' | ')
}

/**
 * Calls per tool name across the request's prior turns and the response. Anthropic
 * `server_tool_use` blocks aren't typed by the renderer (they fall through as JSON text),
 * so their name is read back from that text.
 */
export function countToolCalls(views: (RenderedView | null | undefined)[]): Map<string, number> {
  const counts = new Map<string, number>()
  const add = (name: string) => counts.set(name, (counts.get(name) ?? 0) + 1)
  for (const view of views) {
    for (const message of view?.messages ?? []) {
      for (const block of message.blocks) {
        if (block.kind === 'toolUse') add(block.name)
        else if (block.kind === 'text' && block.text.includes('"server_tool_use"')) {
          try {
            const parsed = JSON.parse(block.text)
            if (parsed?.type === 'server_tool_use' && typeof parsed.name === 'string') add(parsed.name)
          } catch {
            // Not a JSON block.
          }
        }
      }
    }
  }
  return counts
}

function str(v: unknown): string | undefined {
  return typeof v === 'string' ? v : undefined
}

function isRecord(v: unknown): v is Record<string, unknown> {
  return typeof v === 'object' && v !== null && !Array.isArray(v)
}
