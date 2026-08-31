import { createElement, type ReactNode } from 'react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { api } from '@/api/client'
import { EMPTY_FILTERS, type RequestFilters } from '@/api/types'
import { ExportControl } from './ExportControl'

afterEach(() => {
  cleanup()
  vi.restoreAllMocks()
})

function renderControl(filters: RequestFilters = EMPTY_FILTERS) {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: Infinity } },
  })
  const wrapper = ({ children }: { children: ReactNode }) =>
    createElement(QueryClientProvider, { client: queryClient }, children)
  render(createElement(ExportControl, { scope: 42, filters }), { wrapper })
}

describe('ExportControl (#24)', () => {
  it('shows the exact scoped count and builds a current-filter download', async () => {
    const filters: RequestFilters = {
      ...EMPTY_FILTERS,
      q: 'tool result',
      backend: 'openai',
      format: 'openai-responses',
      tag: 'agent-a',
      status: 'error',
      warnedOnly: true,
    }
    vi.spyOn(api, 'getExportCount').mockResolvedValue({ count: 17 })
    renderControl(filters)

    fireEvent.click(screen.getByRole('button', { name: 'Export' }))
    expect(await screen.findByText('17 requests will be exported.')).toBeTruthy()
    expect(api.getExportCount).toHaveBeenCalledWith(42, filters)

    const link = screen.getByRole('link', { name: 'Export JSONL' }) as HTMLAnchorElement
    const url = new URL(link.href)
    expect(url.pathname).toBe('/vessel/api/export')
    expect(Object.fromEntries(url.searchParams)).toMatchObject({
      format: 'jsonl',
      bodies: 'none',
      session: '42',
      q: 'tool result',
      backend: 'openai',
      requestFormat: 'openai-responses',
      tag: 'agent-a',
      status: 'error',
      warned: '1',
    })
  })

  it('offers full bodies for JSONL and prevents that tier for CSV', async () => {
    vi.spyOn(api, 'getExportCount').mockResolvedValue({ count: 1 })
    renderControl()
    fireEvent.click(screen.getByRole('button', { name: 'Export' }))
    await screen.findByText('1 request will be exported.')

    const bodies = screen.getByLabelText('Export bodies') as HTMLSelectElement
    fireEvent.change(bodies, { target: { value: 'full' } })
    expect((screen.getByRole('link', { name: 'Export JSONL' }) as HTMLAnchorElement).href).toContain('bodies=full')

    fireEvent.change(screen.getByLabelText('Export format'), { target: { value: 'csv' } })
    expect(bodies.value).toBe('text')
    expect(Array.from(bodies.options).map((option) => option.value)).toEqual(['none', 'text'])
    expect((screen.getByRole('link', { name: 'Export CSV' }) as HTMLAnchorElement).href).toContain('bodies=text')
  })
})
