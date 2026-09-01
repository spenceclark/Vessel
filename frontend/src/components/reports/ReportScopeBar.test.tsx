import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { EMPTY_FILTERS, type RequestFilters } from '@/api/types'
import { ReportScopeBar } from './ReportScopeBar'

/**
 * Phase 7 D11 — a silently-filtered chart is a lie: active filters render as chips here,
 * each individually clearable, plus Clear filters; with no filters the bar shows the
 * session name alone.
 */

afterEach(cleanup)

const FILTERED: RequestFilters = {
  q: 'timeout',
  backend: 'ollama',
  model: 'qwen3:32b',
  format: null,
  tag: 'planner',
  status: 'error',
  warnedOnly: true,
}

describe('ReportScopeBar (phase 7 D11)', () => {
  it('renders the session name alone when nothing is filtered', () => {
    render(
      <ReportScopeBar sessionLabel="run-42" filters={EMPTY_FILTERS} onFiltersChange={() => {}} />,
    )
    expect(screen.getByText('run-42')).toBeTruthy()
    expect(screen.queryByRole('button')).toBeNull()
  })

  it('renders one chip per active filter, each individually clearable', () => {
    const onFiltersChange = vi.fn()
    render(<ReportScopeBar sessionLabel="run-42" filters={FILTERED} onFiltersChange={onFiltersChange} />)

    for (const label of ['"timeout"', 'backend: ollama', 'model: qwen3:32b', 'tag: planner', 'status: error', 'warnings only']) {
      expect(screen.getByText(label)).toBeTruthy()
    }

    fireEvent.click(screen.getByRole('button', { name: 'Clear filter model: qwen3:32b' }))
    expect(onFiltersChange).toHaveBeenCalledWith({ ...FILTERED, model: null })
  })

  it('Clear filters resets the same filters state App owns', () => {
    const onFiltersChange = vi.fn()
    render(<ReportScopeBar sessionLabel="run-42" filters={FILTERED} onFiltersChange={onFiltersChange} />)

    fireEvent.click(screen.getByRole('button', { name: 'Clear filters' }))
    expect(onFiltersChange).toHaveBeenCalledWith({ ...EMPTY_FILTERS })
  })
})