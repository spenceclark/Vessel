import { createElement } from 'react'
import { cleanup, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it } from 'vitest'
import { ToolCallCard } from './ToolCallCard'

/**
 * #85 — a plain-object tool payload renders as fields with real newlines; everything else
 * keeps the pretty-printed / verbatim rendering.
 */

afterEach(cleanup)

function renderCard(content: string) {
  render(createElement(ToolCallCard, { kind: 'result', content }))
}

describe('ToolCallCard content', () => {
  it('renders a flat object as fields, with multi-line strings unescaped', () => {
    renderCard(
      JSON.stringify({
        code: 'import pandas as pd\n\nprint("hi")',
        stdout: '=== Summary ===\nrows 3',
        stderr: '',
        exit_code: 0,
        timed_out: false,
        files: [],
      }),
    )

    const pres = [...document.querySelectorAll('pre')].map((p) => p.textContent)
    expect(pres).toContain('import pandas as pd\n\nprint("hi")')
    expect(pres).toContain('=== Summary ===\nrows 3')
    expect(document.body.textContent).not.toContain('\\n')

    for (const [key, value] of [
      ['stderr', '""'],
      ['exit_code', '0'],
      ['timed_out', 'false'],
      ['files', '[]'],
    ]) {
      expect(screen.getByText(key).nextElementSibling?.textContent).toBe(value)
    }
  })

  it('pretty-prints a nested object value under its key', () => {
    renderCard(JSON.stringify({ options: { a: 1 } }))

    expect(screen.getByText('options').nextElementSibling?.textContent).toBe('{\n  "a": 1\n}')
  })

  it('keeps arrays pretty-printed as JSON', () => {
    renderCard('[1,2]')
    expect(document.querySelector('pre')!.textContent).toBe('[\n  1,\n  2\n]')
  })

  it('keeps primitives and unparseable text as before', () => {
    renderCard('"just a string"')
    expect(document.querySelector('pre')!.textContent).toBe('"just a string"')
    cleanup()

    renderCard('not json {')
    expect(document.querySelector('pre')!.textContent).toBe('not json {')
  })

  it('keeps an empty object as JSON', () => {
    renderCard('{}')
    expect(document.querySelector('pre')!.textContent).toBe('{}')
  })
})
