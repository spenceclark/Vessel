// Hand-mirrored TS types for the Vessel API (D3/D6). Keep in sync with the C# records in
// src/Vessel/Storage/Summary.cs and src/Vessel/Capture/CaptureEvents.cs by hand — the API
// is small enough this phase that codegen isn't worth the tooling.

export interface Summary {
  id: number
  startedAt: string
  sessionId: number | null
  backend: string
  tags: string[]
  method: string
  path: string
  format: string
  model: string | null
  statusCode: number | null
  error: string | null
  streamed: boolean
  replayOf: number | null
  durationMs: number | null
  ttftMs: number | null
  vesselOverheadMs: number | null
  tokPerSec: number | null
  tokensIn: number | null
  tokensOut: number | null
  tokensCachedRead: number | null
  tokensCachedWrite: number | null
  tokensEstimated: boolean
  stopReason: string | null
  warnings: string[]
  truncated: boolean
}

export interface RequestListResponse {
  rows: Summary[]
  nextBefore: number | null
}

export interface BodyPayload {
  text?: string
  base64?: string
}

export type HeaderMap = Record<string, string[]>

export interface RequestDetail extends Summary {
  requestHeaders: HeaderMap | null
  responseHeaders: HeaderMap | null
  requestBody: BodyPayload | null
  responseBody: BodyPayload | null
  responseRaw: BodyPayload | null
}

export interface StatsResponse {
  total: number
  failed: number
  avgDurationMs: number | null
  avgTokPerSec: number | null
  avgTtftMs: number | null
  sessionId: number | null
  sessionStartedAt: string | null
  tokensIn: number
  tokensOut: number
  tokensCachedRead: number
  tokensCachedWrite: number
  tokensEstimated: boolean
}

export interface SessionInfo {
  id: number
  startedAt: string
  name: string | null
}

export interface StatusBackend {
  name: string
  baseUrl: string
  type: string
  default: boolean
}

export interface StatusPayload {
  name: string
  version: string
  listen: string
  defaultBackend: string
  backends: StatusBackend[]
}

/** The `session` scope this UI is currently viewing: a specific session's id, or "all" history. */
export type SessionScope = number | 'all'

// Phase 4 — filters, facets, clear, config.

export interface RequestFilters {
  q: string
  backend: string | null
  model: string | null
  format: string | null
  tag: string | null
  status: 'all' | 'ok' | 'error'
  warnedOnly: boolean
}

export const EMPTY_FILTERS: RequestFilters = {
  q: '',
  backend: null,
  model: null,
  format: null,
  tag: null,
  status: 'all',
  warnedOnly: false,
}

export function filtersActive(f: RequestFilters): boolean {
  return f.q !== '' || f.backend !== null || f.model !== null || f.format !== null
    || f.tag !== null || f.status !== 'all' || f.warnedOnly
}

export interface FacetsResponse {
  backends: string[]
  models: string[]
  tags: string[]
  formats: string[]
}

export interface ClearResponse {
  deleted: number
}

export interface BackendConfigDto {
  baseUrl: string
  type: string
  injectStreamUsage?: boolean
}

export interface VesselConfigDto {
  listen: string
  defaultBackend: string
  backends: Record<string, BackendConfigDto>
  timeouts: { activitySeconds: number }
  retention: { maxRequests: number; maxDbSizeMb: number }
  capture: { maxBodyMb: number }
  warnings: { slowTtftMs: number }
}

export interface ConfigApplyResult {
  applied: boolean
  restartRequired: string[]
}

// SSE lifecycle events (D5).

export interface StartedEvent {
  seq: number
  startedAt: string
  method: string
  path: string
  backend: string
  tags: string[]
}

export interface RequestReadyEvent {
  seq: number
  model: string
}

export interface FirstTokenEvent {
  seq: number
  ttftMs: number
}

export interface CompletedEvent {
  seq: number
  row: Summary | null
}
