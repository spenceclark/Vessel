import type { HeaderMap } from '@/api/types'

/** Case-insensitive header lookup — the header's first value, or undefined if absent. */
export function findHeader(headers: HeaderMap | null, name: string): string | undefined {
  if (!headers) return undefined
  const target = name.toLowerCase()
  return Object.entries(headers).find(([key]) => key.toLowerCase() === target)?.[1]?.[0]
}
