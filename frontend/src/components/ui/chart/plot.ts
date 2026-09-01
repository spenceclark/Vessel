/**
 * Chart geometry, shared as plain values so this file stays importable from components
 * without tripping the fast-refresh "only components" rule. The plot-area math ChartFrame
 * applies is exported so scale-owning charts (LineChart, BarChart) compute their tick
 * positions from exactly the same box — two measurements of one container must never
 * disagree about where the axes are.
 */

/** D6 — the two-value fixed-height set (§2.3): no arbitrary chart heights. */
export type ChartHeight = 240 | 180

export interface Margins {
  top: number
  right: number
  bottom: number
  left: number
}

export const DEFAULT_MARGINS: Margins = { top: 10, right: 14, bottom: 22, left: 46 }

export interface PlotSize {
  width: number
  height: number
}

export function plotArea(containerWidth: number, height: ChartHeight, margins: Margins = DEFAULT_MARGINS): PlotSize {
  return {
    width: Math.max(0, containerWidth - margins.left - margins.right),
    height: Math.max(0, height - margins.top - margins.bottom),
  }
}