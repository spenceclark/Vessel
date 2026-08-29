import { useQuery } from '@tanstack/react-query'
import { ShieldAlert, Info } from 'lucide-react'
import { api } from '@/api/client'

/** Phase 6 D6 — exposure follows Kestrel's actual bound listener, never the saved config. */
export function BindAddressBanner() {
  const statusQuery = useQuery({
    queryKey: ['status'],
    queryFn: api.getStatus,
    staleTime: 5_000,
    refetchInterval: 5_000,
  })
  const status = statusQuery.data

  if (!status?.listenSecurity.isNonLoopback) return null

  const isContainer = status.listenSecurity.isContainer
  const message = isContainer
    ? `Vessel is listening on ${status.listen} inside a container.`
    : `Vessel is listening on ${status.listen} — anyone on your network can read captured prompts${status.mcp.enabled ? ', and MCP clients can reach /vessel/mcp' : ''}.`

  return (
    <div
      role="status"
      className={isContainer
        ? 'flex items-center gap-2 rounded-control border border-info/30 bg-[color-mix(in_srgb,var(--color-info)_14%,transparent)] px-3 py-2 text-sm text-text-secondary'
        : 'flex items-center gap-2 rounded-control border border-warn/30 bg-[color-mix(in_srgb,var(--color-warn)_14%,transparent)] px-3 py-2 text-sm text-text'}
    >
      {isContainer ? <Info className="h-4 w-4 shrink-0 text-info" aria-hidden="true" /> : <ShieldAlert className="h-4 w-4 shrink-0 text-warn" aria-hidden="true" />}
      <span>{message}</span>
    </div>
  )
}
