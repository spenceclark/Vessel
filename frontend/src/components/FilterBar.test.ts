import { createElement, type ReactNode } from 'react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, fireEvent, cleanup, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { api } from '@/api/client'
import { EMPTY_FILTERS } from '@/api/types'
import { FilterBar, TagPicker } from './FilterBar'

/**
 * R12 — the review's failing case: 100 distinct tags in an unbounded wrapping row could
 * squeeze the virtualized request list down to nothing in the fixed-height list panel.
 * The layout guarantee itself (max-height + internal scroll) is a CSS property, verified
 * live against the running app; this pins the *behavioral* half — the collapsed-by-default
 * "+N more" count and active-first ordering — at the review's own 0/1/100 tag counts.
 */

afterEach(() => {
  cleanup()
  vi.restoreAllMocks()
})

describe('FilterBar action placement (#24 follow-up)', () => {
  it('places Export at the right edge of the search row, outside the filter flow', () => {
    vi.spyOn(api, 'getFacets').mockResolvedValue({ backends: [], models: [], tags: [], formats: [] })
    const queryClient = new QueryClient({
      defaultOptions: { queries: { retry: false, gcTime: Infinity } },
    })
    const wrapper = ({ children }: { children: ReactNode }) =>
      createElement(QueryClientProvider, { client: queryClient }, children)
    render(createElement(FilterBar, {
      scope: 1,
      filters: EMPTY_FILTERS,
      onFiltersChange: () => {},
    }), { wrapper })

    const searchRow = screen.getByTestId('search-export-row')
    const filterRow = screen.getByTestId('filter-controls-row')
    expect(screen.getByTestId('search-input-slot').classList.contains('flex-1')).toBe(true)
    expect(within(searchRow).getByPlaceholderText('Search prompts & responses…')).toBeTruthy()
    expect(within(searchRow).getByRole('button', { name: 'Export' })).toBeTruthy()
    expect(within(searchRow).queryByRole('button', { name: 'Warnings only' })).toBeNull()
    expect(within(filterRow).getByRole('button', { name: 'Warnings only' })).toBeTruthy()
    expect(within(filterRow).queryByRole('button', { name: 'Export' })).toBeNull()
  })
})

describe('TagPicker (R12)', () => {
  it('renders nothing extra for zero tags', () => {
    render(createElement(TagPicker, { tags: [], activeTag: null, onSelect: () => {} }))
    expect(screen.queryByRole('button')).toBeNull()
  })

  it('renders a single tag with no "+N more" expander', () => {
    render(createElement(TagPicker, { tags: ['solo'], activeTag: null, onSelect: () => {} }))
    expect(screen.getByText('solo')).toBeTruthy()
    expect(screen.queryByText(/more/i)).toBeNull()
  })

  it('collapses 100 tags to the first 12 plus a "+88 more" expander, which reveals the rest on click', () => {
    const tags = Array.from({ length: 100 }, (_, i) => `tag-${String(i).padStart(3, '0')}`)
    render(createElement(TagPicker, { tags, activeTag: null, onSelect: () => {} }))

    // Collapsed: exactly the first 12, per facet order.
    for (let i = 0; i < 12; i++) {
      expect(screen.getByText(tags[i])).toBeTruthy()
    }
    expect(screen.queryByText(tags[12])).toBeNull()
    expect(screen.getByText('+88 more')).toBeTruthy()

    fireEvent.click(screen.getByText('+88 more'))

    // Expanded: all 100 present, and the expander becomes a collapse control.
    for (const tag of tags) {
      expect(screen.getByText(tag)).toBeTruthy()
    }
    expect(screen.getByText('Show less')).toBeTruthy()
  })

  it('always shows the active tag first, even collapsed past position 12', () => {
    const tags = Array.from({ length: 100 }, (_, i) => `tag-${String(i).padStart(3, '0')}`)
    render(createElement(TagPicker, { tags, activeTag: 'tag-050', onSelect: () => {} }))

    expect(screen.getByText('tag-050')).toBeTruthy()
    // Still collapsed: only 12 chips plus the expander, not 13 — the active tag displaces
    // one of the trailing facet-order tags rather than being added on top.
    expect(screen.getAllByRole('button').length).toBe(13) // 12 tags + the expander
  })

  it('a long tag name does not prevent selection or the expander from rendering', () => {
    const longName = 'a-very-long-tag-name-that-could-plausibly-wrap-across-several-lines-in-a-narrow-picker'
    const tags = [longName, ...Array.from({ length: 99 }, (_, i) => `tag-${i}`)]
    const selected: string[] = []

    render(createElement(TagPicker, { tags, activeTag: null, onSelect: (t) => selected.push(t ?? '') }))
    fireEvent.click(screen.getByText(longName))

    expect(selected).toEqual([longName])
  })
})
