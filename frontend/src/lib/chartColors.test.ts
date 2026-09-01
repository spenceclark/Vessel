import { describe, expect, it } from 'vitest'
import { assignSeriesColors, CHART_RAMP } from './chartColors'
import { tagVariantIndex } from './tags'

describe('assignSeriesColors (phase 7 D7)', () => {
  it('colors a tag series from its own hash, so a tag matches its pill', () => {
    expect(assignSeriesColors(['planner'], true)).toEqual([CHART_RAMP[tagVariantIndex('planner')]])
  })

  it('steps to the next unused ramp entry on a hash collision — deterministically', () => {
    // Find two real tag strings that hash to the same ramp index; the second must step.
    const seen = new Map<number, string>()
    let first: string | null = null
    let second: string | null = null
    for (let i = 0; i < 500 && second === null; i++) {
      const tag = `tag-${i}`
      const index = tagVariantIndex(tag)
      if (seen.has(index)) {
        first = seen.get(index)!
        second = tag
      } else {
        seen.set(index, tag)
      }
    }
    expect(first).not.toBeNull()
    expect(second).not.toBeNull()

    const firstIndex = tagVariantIndex(first!)
    const colors = assignSeriesColors([first!, second!], true)
    expect(colors[0]).toBe(CHART_RAMP[firstIndex])
    expect(colors[1]).toBe(CHART_RAMP[(firstIndex + 1) % CHART_RAMP.length])
  })

  it('colors non-tag groupings and the unkeyed series by ramp order, by rank', () => {
    expect(assignSeriesColors([null, 'm1', 'm2'], false)).toEqual([CHART_RAMP[0], CHART_RAMP[1], CHART_RAMP[2]])
    expect(assignSeriesColors([null], true)).toEqual([CHART_RAMP[0]])
  })
})