import type {
  RequestDetail,
  RequestListResponse,
  SessionInfo,
  SessionScope,
  StatsResponse,
  StatusPayload,
} from './types'

const BASE = '/vessel/api'

export class ApiError extends Error {
  status: number
  code: string

  constructor(status: number, code: string, message: string) {
    super(message)
    this.status = status
    this.code = code
  }
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(`${BASE}${path}`, init)
  if (!res.ok) {
    let code = 'unknown'
    let message = res.statusText
    try {
      const body = (await res.json()) as { error?: { code?: string; message?: string } }
      code = body.error?.code ?? code
      message = body.error?.message ?? message
    } catch {
      // Not a Vessel-shaped JSON error body — fall back to the status text.
    }

    throw new ApiError(res.status, code, message)
  }

  if (res.status === 204) {
    return undefined as T
  }

  return (await res.json()) as T
}

export interface ListRequestsParams {
  limit?: number
  before?: number
  session?: SessionScope
}

/** `/stats` additionally accepts "current" (its server-side default) — /requests never does. */
export type StatsSessionParam = SessionScope | 'current'

export const api = {
  getStatus: () => request<StatusPayload>('/status'),

  listRequests: ({ limit, before, session }: ListRequestsParams = {}) => {
    const params = new URLSearchParams()
    if (limit !== undefined) params.set('limit', String(limit))
    if (before !== undefined) params.set('before', String(before))
    // "all" means "no session filter" for /requests (unlike /stats, which has a
    // dedicated "all" keyword) — D3 only wires limit/before/session as a literal id.
    if (session !== undefined && session !== 'all') params.set('session', String(session))
    const qs = params.toString()
    return request<RequestListResponse>(`/requests${qs ? `?${qs}` : ''}`)
  },

  getRequest: (id: number) => request<RequestDetail>(`/requests/${id}`),

  getStats: (session?: StatsSessionParam) =>
    request<StatsResponse>(`/stats${session !== undefined ? `?session=${session}` : ''}`),

  listSessions: () => request<SessionInfo[]>('/sessions'),

  createSession: (name?: string) =>
    request<SessionInfo>('/sessions', {
      method: 'POST',
      headers: name ? { 'Content-Type': 'application/json' } : undefined,
      body: name ? JSON.stringify({ name }) : undefined,
    }),
}
