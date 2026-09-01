import { tagVariantIndex } from './tags'

/**
 * ui-spec §2.3 — the categorical series ramp, --chart-1..6, in ramp order. Consumed as
 * var() attribute values only, never as JS color computations (that is what makes a theme
 * flip free, and why canvas was rejected for charts).
 */
export const CHART_RAMP = [
  'var(--color-chart-1)',
  'var(--color-chart-2)',
  'var(--color-chart-3)',
  'var(--color-chart-4)',
  'var(--color-chart-5)',
  'var(--color-chart-6)',
] as const

/**
 * Phase 7 D7 — one color per series, deterministic because series arrive rank-ordered
 * from the server.
 *
 * - groupBy=tag: color from the tag's own hash, so a tag's line is its pill's color
 *   everywhere (§6's promise). Two tags can hash to the same hue; a collision inside one
 *   chart steps to the next unused ramp entry (wrapping).
 * - Any other grouping, and the single unkeyed series: the ramp in order, by rank index.
 */
export function assignSeriesColors(keys: (string | null)[], groupByTag: boolean): string[] {
  if (!groupByTag) {
    return keys.slice(0, CHART_RAMP.length).map((_, index) => CHART_RAMP[index]!)
  }

  const used = new Set<number>()
  const colors: string[] = []
  for (const key of keys.slice(0, CHART_RAMP.length)) {
    let index = key === null ? 0 : tagVariantIndex(key)
    while (used.has(index)) {
      index = (index + 1) % CHART_RAMP.length
    }
    used.add(index)
    colors.push(CHART_RAMP[index]!)
  }
  return colors
}