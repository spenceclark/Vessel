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
  isCurrent: boolean
  requestCount: number
  lastRequestAt: string | null
}

export interface StatusBackend {
  name: string
  baseUrl: string
  type: string
  default: boolean
  authEnv?: string
  health: BackendHealth
}

export interface BackendHealth {
  state: 'green' | 'red' | 'unknown'
  lastSeenAt: string | null
}

/** R06 — whether the background writer is still recording (a give-up used to be a log line only). */
export interface CaptureHealth {
  recording: boolean
  stoppedReason?: string
}

/**
 * #11 — first-run setup state. `firstRun` is true only for the process that created
 * `vessel.json`; `defaultBackendReachable` is that run's one-shot probe of the default
 * backend and is `null` on every later run (no probe ran). Distinct from `BackendHealth`,
 * which stays passively observed from captured traffic.
 */
export interface SetupStatus {
  firstRun: boolean
  defaultBackendReachable: boolean | null
}

export interface StatusPayload {
  name: string
  version: string
  listen: string
  defaultBackend: string
  backends: StatusBackend[]
  capture: CaptureHealth
  mcp: { enabled: boolean }
  listenSecurity: { isNonLoopback: boolean; isContainer: boolean }
  /** H0b — this Vessel process's run id (a restart changes it). */
  serverRunId: string
  setup: SetupStatus
}

/**
 * #11 — whether the first-run probe's "nothing was listening" still stands. The two signals
 * on `/status` age differently: passive health is re-derived from every captured outcome and
 * is always current, while the probe answers once, at startup, and is never refreshed. So one
 * successful request (`green`) is a newer and better answer and supersedes it — without this,
 * `first run with Ollama down → start Ollama → request succeeds → Reset session` would leave
 * an empty list insisting a plainly working backend isn't responding. `red` supersedes
 * nothing: it agrees with the probe.
 */
export function firstRunProbeSaysUnreachable(status: StatusPayload | undefined): boolean {
  const health = status?.backends.find((backend) => backend.default)?.health.state
  return status?.setup.defaultBackendReachable === false && health !== 'green'
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

export type ExportFormat = 'csv' | 'jsonl'

export type ExportBodies = 'none' | 'text' | 'full'

export interface ExportCountResponse {
  count: number
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
  /** R23/H0a — count deleted, for the UX toast only; the client purges cleared rows on the in-band `cleared` SSE event, not on a boundary in this ack. */
  deleted: number
}

// Phase 7 — chart read endpoints (phase-7-charts.md D1/D2).

export type SeriesMetricName = 'tokens_in' | 'tokens_out' | 'tokens_total'

export type SeriesGroupByName = 'none' | 'tag' | 'model' | 'backend'

export type AggregateDimensionName = 'model' | 'tag' | 'backend' | 'format' | 'warning'

/** D1 — one chart point: the request's id (so a click can select it), ISO started_at, value. */
export interface SeriesPoint {
  id: number
  t: string
  v: number
}

/** D1 — one named series; `key: null` renders as "(none)" (untagged / model-less). */
export interface SeriesGroup {
  key: string | null
  points: SeriesPoint[]
}

export interface SeriesResponse {
  metric: SeriesMetricName
  groupBy: SeriesGroupByName
  series: SeriesGroup[]
  returned: number
  /** Computed only when the point cap was hit; otherwise 0. */
  totalMatching: number
  truncated: boolean
  /** Series dropped (never merged) past the six-series ramp cap. */
  omittedSeries: number
  /** Any drawn row had estimated token counts — the whole chart is approximate. */
  estimated: boolean
}

export interface AggregateRow {
  key: string | null
  requests: number
  failed: number
  tokensIn: number
  tokensOut: number
  tokensCachedRead: number
  tokensCachedWrite: number
  avgDurationMs: number | null
  /** Streamed rows only, mirroring /stats. */
  avgTtftMs: number | null
  avgTokPerSec: number | null
  tokensEstimated: boolean
  /** #26 live-use feedback — nearest-rank percentiles over the group's non-null durations. */
  p50DurationMs: number | null
  p95DurationMs: number | null
}

export interface AggregateResponse {
  by: AggregateDimensionName
  rows: AggregateRow[]
  totalGroups: number
}

/** #41 — optional predicate on an ordered clear frame; absent for all/before clears. */
export interface ClearedEvent {
  sessionId?: number
}

export type RequestClearScope = { all: true } | { before: string }

/**
 * #29 — mirrors `SessionLimits.MaxMarkers` (src/Vessel/Storage/Summary.cs): `GET /sessions`
 * returns the current marker plus at most this many rows. A response *at* the limit may
 * therefore be truncated, so a session missing from it is not evidence that it was deleted.
 */
export const SESSION_LIST_LIMIT = 500

export interface SessionDeleteSummary {
  sessionsDeleted: number
  requestsDeleted: number
  failures: { sessionId: number; message: string }[]
}

/**
 * R11/F2/J0/K0b — the recovery snapshot: lifecycle truth as of one position in the event log.
 * `active` is the server's in-flight set — each entry carrying the metadata its `started` frame
 * carried, so the client can *render* it — and `logPosition` is the SSE publish id that set is
 * true as of, both read in one critical section. The client rebuilds its in-flight rows from
 * `active` wholesale and discards every event it is holding at or below `logPosition` — those
 * are already reflected here, and in the database the refetch that follows reads — replaying
 * only what came after. `serverRunId` (H0b) identifies the process lifetime: seqs *and*
 * positions reset with the process, so a snapshot from another run is discarded outright
 * rather than compared against.
 */
export interface ActiveRequestsResponse {
  active: ActiveDescriptor[]
  logPosition: number
  serverRunId: string
}

/**
 * K0b/R11/R27 — one in-flight request as the recovery snapshot describes it: its `seq` plus
 * the payload of its `started` frame, `model` once `request_ready` has parsed one (null until
 * then, and for bodies with no parseable model), and `ttftMs` once `first_token` has fired
 * (null until then, and for a request still waiting on its first byte).
 *
 * The snapshot carries these because a bare seq cannot be displayed, and the frame that would
 * have supplied the rest — including the live TTFT a `first_token` frame measured — is exactly
 * the one a lossy subscriber queue may have dropped: a monitor that knows a request is running
 * but cannot show its measured progress is not monitoring it.
 */
export interface ActiveDescriptor {
  seq: number
  startedAt: string
  sessionId: number | null
  sessionName: string | null
  method: string
  path: string
  backend: string
  tags: string[]
  replayOf: number | null
  model: string | null
  ttftMs: number | null
}

export interface BackendConfigDto {
  baseUrl: string
  type: string
  injectStreamUsage?: boolean
  authEnv?: string
}

export interface VesselConfigDto {
  listen: string
  defaultBackend: string
  backends: Record<string, BackendConfigDto>
  timeouts: { activitySeconds: number }
  retention: { maxRequests: number; maxDbSizeMb: number }
  capture: { maxBodyMb: number }
  warnings: { slowTtftMs: number }
  mcp: { enabled: boolean }
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
  /** D05/#29 — known for headerless traffic; null until writer resolution for a named request. */
  sessionId: number | null
  sessionName: string | null
  method: string
  path: string
  backend: string
  tags: string[]
  replayOf: number | null
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

/**
 * H0b(1) — the first SSE frame on every connection, carrying this process's run id. A run-id
 * change across a reconnect means Vessel restarted and the client's in-flight seqs are from a
 * dead process, so it discards them wholesale. Carries no `id:` field, so it never affects the
 * gap-detection watermark.
 */
export interface HelloEvent {
  serverRunId: string
}

/**
 * H0a/R23/J0 — the in-band clear notification, ordered on the SSE stream against completions:
 * history was deleted at this frame's position. All/before clears carry `{}`; #41's session
 * deletion carries `{sessionId}` so a client can remove exactly that session's cached rows and
 * completion buffer at that position. The server retains no clear history; a client that missed it
 * recovers by snapshot, whose refetch reads a database that already reflects every clear. The
 * retired I0a payload (`{version, scope, beforeTs, boundaryId}`) asked the client to decide
 * which rows a past deletion had removed, which no client-side predicate can do correctly
 * across missed clears and SQLite id reuse (round-five review §2.2).
 */
