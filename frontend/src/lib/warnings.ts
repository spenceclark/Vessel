// Mirrors src/Vessel/Formats/Warnings.cs's code vocabulary — one label per stored code.
const WARNING_LABELS: Record<string, string> = {
  truncated_response: 'Truncated response',
  http_error: 'HTTP error',
  proxy_error: 'Proxy error',
  client_disconnect: 'Client disconnected',
  tokens_estimated: 'Tokens estimated',
  stream_incomplete: 'Stream incomplete',
  parse_error: 'Parse error',
  body_truncated: 'Body truncated',
  cold_load: 'Cold model load',
  slow_ttft: 'Slow TTFT',
  usage_injected: 'Usage injected',
  tool_call_in_text: 'Tool call in text',
  path_missing_v1: 'Missing /v1 in base_url',
}

export function warningLabel(code: string): string {
  return WARNING_LABELS[code] ?? code
}

// One-line explanations shown above the badge in the detail Overview (issue #57) — most
// warning codes are self-explanatory from the label alone, so this stays sparse.
const WARNING_HINTS: Record<string, string> = {
  path_missing_v1: "OpenAI-compatible backends expect /v1 in the client's base_url.",
}

export function warningHint(code: string): string | undefined {
  return WARNING_HINTS[code]
}

// ui-spec.md §6 — info-class codes render info-colored regardless of the row's error
// state; everything else follows the row (danger if the row is an error, warn otherwise).
const INFO_CODES = new Set(['tokens_estimated', 'usage_injected'])

export function warningVariant(code: string, isError: boolean): 'info' | 'danger' | 'warn' {
  if (INFO_CODES.has(code)) return 'info'
  return isError ? 'danger' : 'warn'
}
