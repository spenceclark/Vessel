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
  /** R05 remainder — this body is a prefix: display-time decoding hit the capture budget. Distinct from capture-time `truncated` (Summary.truncated / the `body_truncated` warning code). */
  decodeTruncated?: boolean
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

/** R06 — whether the background writer is still recording (a give-up used to be a log line only). */
export interface CaptureHealth {
  recording: boolean
  stoppedReason?: string
}

export interface StatusPayload {
  name: string
  version: string
  listen: string
  defaultBackend: string
  backends: StatusBackend[]
  capture: CaptureHealth
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
  /** R23 — the highest deleted id, so a buffered completion above it survives a clear-before; omitted when nothing matched. */
  boundaryId?: number
}

/**
 * R11/F2 — the server's authoritative in-flight set. Reconciliation removes any client-side
 * in-flight row whose seq is absent here and at or below `newestCompletedSeq`; the boundary
 * spares a request that started after this snapshot was taken.
 */
export interface ActiveRequestsResponse {
  activeSeqs: number[]
  newestCompletedSeq: number
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

// R16: GET returns the persisted restart state alongside the config, so reopening the
// panel shows a still-pending restart even without a fresh PUT response to remember it.
export interface ConfigGetResponse {
  config: VesselConfigDto
  restartRequired: string[]
}

// SSE lifecycle events (D5).

export interface StartedEvent {
  seq: number
  startedAt: string
  /** D05 — known at request start, so in-flight rows can be scoped to the viewed session. */
  sessionId: number
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
