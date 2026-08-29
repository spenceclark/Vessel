import { createElement, type ReactNode } from 'react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { api } from '@/api/client'
import type { ConfigGetResponse } from '@/api/types'
import { ConfigPanel } from './ConfigPanel'

const TYPE_EXPLAINER = 'auto = detect from traffic; observation only — typed backends unlock replay targeting and correct replay auth.'

afterEach(() => {
  cleanup()
  delete document.documentElement.dataset.theme
})

function renderConfigPanel(theme: 'light' | 'dark') {
  document.documentElement.dataset.theme = theme
  const response: ConfigGetResponse = {
    config: {
      listen: '127.0.0.1:4550',
      defaultBackend: 'ollama',
      backends: { ollama: { baseUrl: 'http://localhost:11434', type: 'ollama' } },
      timeouts: { activitySeconds: 1800 },
      retention: { maxRequests: 10_000, maxDbSizeMb: 500 },
      capture: { maxBodyMb: 32 },
      warnings: { slowTtftMs: 1000 },
    },
    restartRequired: [],
  }
  vi.spyOn(api, 'getConfig').mockResolvedValue(response)

  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: Infinity, staleTime: Infinity } },
  })
  const wrapper = ({ children }: { children: ReactNode }) =>
    createElement(QueryClientProvider, { client: queryClient }, children)
  render(createElement(ConfigPanel), { wrapper })
}

describe('ConfigPanel backend type explainer (R12)', () => {
  it.each(['light', 'dark'] as const)('renders with muted xs styling in the %s theme', async (theme) => {
    renderConfigPanel(theme)

    const explainer = await screen.findByText(TYPE_EXPLAINER)
    expect(document.documentElement.dataset.theme).toBe(theme)
    expect(explainer.classList.contains('text-xs')).toBe(true)
    expect(explainer.classList.contains('text-text-muted')).toBe(true)
  })
})
