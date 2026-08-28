import { describe, expect, it } from 'vitest'
import { cn } from './utils'

/**
 * ui-spec.md §9.1 — plain tailwind-merge misclassifies the app's custom `text-stat`
 * font-size utility as a text-color utility (it doesn't recognize `stat` as a size), so a
 * trailing color class silently deleted it — every `Stat` value (StatsBar's header
 * figures) rendered at the wrong size. Pins that `cn()`'s custom merge config keeps both.
 */
describe('cn — text-stat / text-color conflict', () => {
  it('keeps text-stat alongside a trailing text color utility', () => {
    const classes = cn('text-stat font-semibold tabular-nums', 'text-danger')
    expect(classes.split(' ')).toContain('text-stat')
    expect(classes.split(' ')).toContain('text-danger')
  })

  it('keeps text-stat alongside text-text (StatsBar\'s non-danger case)', () => {
    const classes = cn('text-stat font-semibold tabular-nums', 'text-text')
    expect(classes.split(' ')).toContain('text-stat')
    expect(classes.split(' ')).toContain('text-text')
  })

  it('still lets a later font-size utility win over an earlier one (real conflicts still resolve)', () => {
    const classes = cn('text-stat', 'text-sm')
    expect(classes.split(' ')).not.toContain('text-stat')
    expect(classes.split(' ')).toContain('text-sm')
  })

  it('still lets a later text color utility win over an earlier one (color conflicts still resolve)', () => {
    const classes = cn('text-danger', 'text-text')
    expect(classes.split(' ')).not.toContain('text-danger')
    expect(classes.split(' ')).toContain('text-text')
  })
})
