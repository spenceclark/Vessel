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
}

export function warningLabel(code: string): string {
  return WARNING_LABELS[code] ?? code
}
