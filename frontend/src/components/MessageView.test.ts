import { createElement } from 'react'
import { render, screen, fireEvent, cleanup } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { MessageView } from './MessageView'
import { tryPrettyJson } from '@/render/prettyJson'
import type { RenderedView } from '@/render'

/**
 * R03/R18 — the policy under test: rendering captured content never makes a network
 * request of its own, and an embedded image previews from its own captured bytes only.
 * A markdown image pointing at a URL (the review's synthetic repro used a *local* stub —
 * the point is "any URL", not "only remote ones") must never become a live, fetchable
 * `<img src>`; a `data:`-embedded image is safe to render directly since it makes no
 * request either way.
 */

afterEach(cleanup)

function view(overrides: Partial<RenderedView>): RenderedView {
  return { messages: [], params: [], ...overrides }
}

describe('MessageView — captured-content resource policy', () => {
  it('a markdown image pointing at a URL never becomes a live <img src>', () => {
    const fetchSpy = vi.spyOn(globalThis, 'fetch')

    render(
      createElement(MessageView, {
        view: view({
          messages: [
            {
              role: 'user',
              blocks: [{ kind: 'markdown', text: '![a review stub image](http://127.0.0.1:9/pixel.png)' }],
            },
          ],
        }),
      }),
    )

    const images = document.querySelectorAll('img')
    for (const img of images) {
      expect(img.src).not.toContain('127.0.0.1:9')
    }

    // The placeholder chip renders instead, with the raw URL surfaced as inert text only
    // after the user opts in — never as a live resource.
    expect(screen.getByText(/preview/i)).toBeTruthy()
    fireEvent.click(screen.getByText(/preview/i))
    expect(screen.getByText('http://127.0.0.1:9/pixel.png')).toBeTruthy()
    expect(document.querySelectorAll('img').length).toBe(0)

    expect(fetchSpy).not.toHaveBeenCalled()
  })

  it('a markdown link never becomes a navigable <a href>', () => {
    render(
      createElement(MessageView, {
        view: view({
          messages: [{ role: 'user', blocks: [{ kind: 'markdown', text: '[click me](http://127.0.0.1:9/x)' }] }],
        }),
      }),
    )

    expect(document.querySelectorAll('a').length).toBe(0)
    expect(screen.getByText('click me')).toBeTruthy()
  })

  it('an image block with an embedded data URI previews from that exact source', () => {
    const dataUri = 'data:image/png;base64,AAAA'

    render(
      createElement(MessageView, {
        view: view({
          messages: [
            { role: 'assistant', blocks: [{ kind: 'image', label: 'a photo', source: { kind: 'embedded', dataUri } }] },
          ],
        }),
      }),
    )

    expect(document.querySelectorAll('img').length).toBe(0) // not shown until clicked
    fireEvent.click(screen.getByText(/preview/i))

    const img = document.querySelector('img')
    expect(img).toBeTruthy()
    expect(img!.src).toBe(dataUri)
  })

  it('an image block with an unknown source is not clickable and previews nothing', () => {
    render(
      createElement(MessageView, {
        view: view({
          messages: [{ role: 'assistant', blocks: [{ kind: 'image', label: 'mystery', source: { kind: 'unknown' } }] }],
        }),
      }),
    )

    expect(screen.queryByText(/preview/i)).toBeNull()
    fireEvent.click(screen.getByText(/mystery/i))
    expect(document.querySelectorAll('img').length).toBe(0)
  })
})

/**
 * ui-spec.md §9.1 — "pretty-print JSON-only text blocks": whole-block only. A block
 * counts as JSON here only if its *entire* trimmed text parses as one JSON object or
 * array; a bare primitive or JSON embedded partway through prose must fall through to
 * ordinary rendering.
 */
describe('tryPrettyJson', () => {
  it('pretty-prints a whole-block JSON object', () => {
    expect(tryPrettyJson('{"a":1,"b":[1,2,3]}')).toBe('{\n  "a": 1,\n  "b": [\n    1,\n    2,\n    3\n  ]\n}')
  })

  it('pretty-prints a whole-block JSON array', () => {
    expect(tryPrettyJson('[1,2,3]')).toBe('[\n  1,\n  2,\n  3\n]')
  })

  it('tolerates surrounding whitespace/newlines around the JSON', () => {
    expect(tryPrettyJson('  \n {"a":1}\n  ')).toBe('{\n  "a": 1\n}')
  })

  it('rejects bare JSON primitives (string, number, boolean, null)', () => {
    expect(tryPrettyJson('"hello"')).toBeNull()
    expect(tryPrettyJson('42')).toBeNull()
    expect(tryPrettyJson('true')).toBeNull()
    expect(tryPrettyJson('null')).toBeNull()
  })

  it('rejects JSON embedded partway through prose', () => {
    expect(tryPrettyJson('Here is the result: {"a":1}')).toBeNull()
    expect(tryPrettyJson('{"a":1} — that\'s the answer')).toBeNull()
  })

  it('rejects malformed JSON', () => {
    expect(tryPrettyJson('{"a":1,}')).toBeNull()
    expect(tryPrettyJson('{unquoted: 1}')).toBeNull()
  })

  it('rejects plain prose and empty text', () => {
    expect(tryPrettyJson('just a normal sentence.')).toBeNull()
    expect(tryPrettyJson('')).toBeNull()
    expect(tryPrettyJson('   ')).toBeNull()
  })
})

describe('MessageView — JSON-only block pretty-printing', () => {
  it('renders a whole-block JSON markdown block as a pretty-printed code block, not markdown', () => {
    render(
      createElement(MessageView, {
        view: view({
          messages: [{ role: 'assistant', blocks: [{ kind: 'markdown', text: '{"city":"Paris","temp_c":18}' }] }],
        }),
      }),
    )

    const pre = document.querySelector('pre')
    expect(pre).toBeTruthy()
    expect(pre!.textContent).toBe('{\n  "city": "Paris",\n  "temp_c": 18\n}')
    // Not run through ReactMarkdown — no .md wrapper div for this block.
    expect(document.querySelector('.md')).toBeNull()
  })

  it('renders a whole-block JSON text block as a pretty-printed code block', () => {
    render(
      createElement(MessageView, {
        view: view({
          messages: [{ role: 'tool', blocks: [{ kind: 'text', text: '{"status":"ok"}' }] }],
        }),
      }),
    )

    const pre = document.querySelector('pre')
    expect(pre!.textContent).toBe('{\n  "status": "ok"\n}')
  })

  it('leaves prose containing JSON as ordinary markdown', () => {
    render(
      createElement(MessageView, {
        view: view({
          messages: [{ role: 'user', blocks: [{ kind: 'markdown', text: 'The result was {"ok":true}.' }] }],
        }),
      }),
    )

    expect(document.querySelector('.md')).toBeTruthy()
    expect(document.querySelector('pre')).toBeNull()
  })
})
