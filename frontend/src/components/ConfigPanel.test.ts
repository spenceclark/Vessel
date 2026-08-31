import { createElement, type ReactNode } from 'react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { api } from '@/api/client'
import type { BackendConfigDto, ConfigGetResponse } from '@/api/types'
import { ConfigPanel } from './ConfigPanel'

// The shared explainers open with a control-name lead-in (<span>Type</span> etc., #42
// follow-up), so a paragraph's own text starts at the em-dash — match the explainer
// sentence as a substring (exact: false) rather than the whole node text.
const TYPE_EXPLAINER = 'auto = detect from traffic; observation only. Typed backends unlock replay targeting and correct replay auth.'
const INJECT_STREAM_USAGE_LABEL = 'Exact token counts (streamed)'
const findByTypeExplainer = () => screen.findByText(TYPE_EXPLAINER, { exact: false })
const getAllByTypeExplainer = () => screen.getAllByText(TYPE_EXPLAINER, { exact: false })

afterEach(() => {
  cleanup()
  delete document.documentElement.dataset.theme
})

function renderConfigPanel(
  theme: 'light' | 'dark',
  backends: Record<string, BackendConfigDto> = { ollama: { baseUrl: 'http://localhost:11434', type: 'ollama' } },
) {
  document.documentElement.dataset.theme = theme
  const response: ConfigGetResponse = {
    config: {
      listen: '127.0.0.1:4550',
      defaultBackend: Object.keys(backends)[0],
      backends,
      timeouts: { activitySeconds: 1800 },
      retention: { maxRequests: 10_000, maxDbSizeMb: 500 },
      capture: { maxBodyMb: 32 },
      warnings: { slowTtftMs: 1000 },
      mcp: { enabled: true },
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

    const explainer = await findByTypeExplainer()
    expect(document.documentElement.dataset.theme).toBe(theme)
    expect(explainer.classList.contains('text-xs')).toBe(true)
    expect(explainer.classList.contains('text-text-muted')).toBe(true)
  })

  it('states the MCP prompt-read boundary beside its toggle', async () => {
    renderConfigPanel('dark')

    expect(await screen.findByText('An MCP client you connect can read captured prompts.')).toBeTruthy()
    expect(screen.getByLabelText('Enable MCP server')).toBeTruthy()
  })
})

describe('ConfigPanel add-backend picker (#9)', () => {
  it('lists every known backend from architecture.md §9 plus a Custom… escape hatch', async () => {
    renderConfigPanel('dark')

    const picker = (await screen.findByLabelText('Add backend')) as HTMLSelectElement
    const optionLabels = Array.from(picker.options).map((o) => o.textContent)
    expect(optionLabels).toEqual([
      'Add backend…',
      'Ollama',
      'LM Studio',
      'llama.cpp',
      'vLLM',
      'Lemonade',
      'Unsloth',
      'OpenAI',
      'Anthropic / Claude',
      'Gemini',
      'Custom…',
    ])
  })

  it('prefills baseUrl/type/authEnv for a known backend, without disturbing the existing row', async () => {
    renderConfigPanel('dark')

    fireEvent.change(await screen.findByLabelText('Add backend'), { target: { value: 'openai' } })

    const nameInputs = await screen.findAllByPlaceholderText('name')
    expect(nameInputs.map((el) => (el as HTMLInputElement).value).sort()).toEqual(['ollama', 'openai'])

    const openaiRow = nameInputs
      .find((el) => (el as HTMLInputElement).value === 'openai')!
      .closest('div.rounded-control') as HTMLElement
    expect((within(openaiRow).getByPlaceholderText('http://localhost:11434') as HTMLInputElement).value).toBe(
      'https://api.openai.com',
    )
    expect((within(openaiRow).getByRole('combobox') as HTMLSelectElement).value).toBe('openai')
    expect(
      (within(openaiRow).getByLabelText('Authentication environment variable for openai') as HTMLInputElement).value,
    ).toBe('OPENAI_API_KEY')
  })

  it('resolves a name collision instead of overwriting the existing row', async () => {
    renderConfigPanel('dark')

    fireEvent.change(await screen.findByLabelText('Add backend'), { target: { value: 'ollama' } })

    const nameInputs = await screen.findAllByPlaceholderText('name')
    expect(nameInputs.map((el) => (el as HTMLInputElement).value).sort()).toEqual(['ollama', 'ollama-2'])
  })

  it('adds a blank, untyped row for "Custom…"', async () => {
    renderConfigPanel('dark')

    fireEvent.change(await screen.findByLabelText('Add backend'), { target: { value: 'custom' } })

    const nameInputs = await screen.findAllByPlaceholderText('name')
    const customRow = nameInputs
      .find((el) => (el as HTMLInputElement).value === 'new-backend')!
      .closest('div.rounded-control') as HTMLElement
    expect((within(customRow).getByPlaceholderText('http://localhost:11434') as HTMLInputElement).value).toBe('')
    expect((within(customRow).getByRole('combobox') as HTMLSelectElement).value).toBe('auto')
  })
})

describe('ConfigPanel injectStreamUsage explainer (#10)', () => {
  it('gives the checkbox a human label and one-line help for an OpenAI-format backend', async () => {
    renderConfigPanel('dark', { openai: { baseUrl: 'https://api.openai.com', type: 'openai' } })

    expect(await screen.findByLabelText(INJECT_STREAM_USAGE_LABEL)).toBeTruthy()
    expect(screen.getByText(/adds/i).textContent).toContain('include_usage')
  })

  it('still shows the control for an auto-typed backend', async () => {
    renderConfigPanel('dark', { mixed: { baseUrl: 'http://localhost:9999', type: 'auto' } })

    expect(await screen.findByLabelText(INJECT_STREAM_USAGE_LABEL)).toBeTruthy()
  })

  it('hides the control for an Anthropic backend, which never reads it', async () => {
    renderConfigPanel('dark', {
      anthropic: { baseUrl: 'https://api.anthropic.com', type: 'anthropic', authEnv: 'ANTHROPIC_API_KEY' },
    })

    await findByTypeExplainer()
    expect(screen.queryByLabelText(INJECT_STREAM_USAGE_LABEL)).toBeNull()
  })

  it('hides the control for an Ollama backend, which never reads it', async () => {
    renderConfigPanel('dark')

    await findByTypeExplainer()
    expect(screen.queryByLabelText(INJECT_STREAM_USAGE_LABEL)).toBeNull()
  })
})

describe('ConfigPanel compact backend cards (#42)', () => {
  it('renders each explainer once at section level, not per backend card', async () => {
    renderConfigPanel('dark', {
      ollama: { baseUrl: 'http://localhost:11434', type: 'ollama' },
      openai: { baseUrl: 'https://api.openai.com', type: 'openai' },
      anthropic: { baseUrl: 'https://api.anthropic.com', type: 'anthropic' },
    })

    await findByTypeExplainer()
    expect(getAllByTypeExplainer()).toHaveLength(1)
    expect(screen.getAllByText(/adds/i)).toHaveLength(1)

    for (const nameInput of screen.getAllByPlaceholderText('name')) {
      const card = nameInput.closest('div.rounded-control') as HTMLElement
      expect(within(card).queryByText(TYPE_EXPLAINER, { exact: false })).toBeNull()
      expect(within(card).queryByText(/adds/i)).toBeNull()
    }
  })

  it('leads each shared explainer with its control name so the paragraphs do not read as orphans', async () => {
    renderConfigPanel('dark', { openai: { baseUrl: 'https://api.openai.com', type: 'openai' } })

    const typeLeadIn = await screen.findByText('Type')
    expect(typeLeadIn.classList.contains('font-[550]')).toBe(true)
    expect(typeLeadIn.classList.contains('text-text-secondary')).toBe(true)
    expect(screen.getByText('Exact token counts')).toBeTruthy()
  })

  it('hides the shared include_usage explainer while no row presents the checkbox', async () => {
    renderConfigPanel('dark', {
      ollama: { baseUrl: 'http://localhost:11434', type: 'ollama' },
      anthropic: { baseUrl: 'https://api.anthropic.com', type: 'anthropic', authEnv: 'ANTHROPIC_API_KEY' },
    })

    await findByTypeExplainer()
    expect(screen.queryByText(/adds/i)).toBeNull()
    expect(screen.queryByLabelText(INJECT_STREAM_USAGE_LABEL)).toBeNull()
  })

  it('labels the default radio "Default" in sentence case', async () => {
    renderConfigPanel('dark')

    const radio = (await screen.findByRole('radio', { name: 'Default' })) as HTMLInputElement
    expect(radio.checked).toBe(true)
  })
})
