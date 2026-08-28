import { createElement } from 'react'
import { render, screen, cleanup } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { RenderErrorBoundary } from './RenderErrorBoundary'

/**
 * R17 — the backstop above `render/validate.ts`: even a rendering crash the sanitizer
 * didn't anticipate must degrade to that one tab's fallback, never the whole app. Pins
 * the second half of "navigation still works" — a fresh `key` (DetailPane keys this by
 * request id) must recover cleanly from a *previous* capture's crash, not stay stuck
 * showing that error.
 */

function Bomb(): never {
  throw new Error('boom')
}

afterEach(cleanup)

describe('RenderErrorBoundary', () => {
  it('renders children when nothing throws', () => {
    render(
      createElement(RenderErrorBoundary, {
        fallback: createElement('div', null, 'fallback'),
        // eslint-disable-next-line react/no-children-prop -- createElement's 3-arg overload doesn't satisfy TS here
        children: createElement('div', null, 'ok'),
      }),
    )
    expect(screen.getByText('ok')).toBeTruthy()
    expect(screen.queryByText('fallback')).toBeNull()
  })

  it('renders the fallback instead of blanking when a child throws', () => {
    const consoleError = vi.spyOn(console, 'error').mockImplementation(() => {})

    render(
      createElement(RenderErrorBoundary, {
        fallback: createElement('div', null, 'raw json fallback'),
        // eslint-disable-next-line react/no-children-prop -- createElement's 3-arg overload doesn't satisfy TS here
        children: createElement(Bomb),
      }),
    )

    expect(screen.getByText('raw json fallback')).toBeTruthy()
    consoleError.mockRestore()
  })

  it('a fresh key (navigating to a different capture) recovers instead of staying crashed', () => {
    const consoleError = vi.spyOn(console, 'error').mockImplementation(() => {})

    const { rerender } = render(
      createElement(RenderErrorBoundary, {
        key: 'request-1',
        fallback: createElement('div', null, 'fallback'),
        // eslint-disable-next-line react/no-children-prop -- createElement's 3-arg overload doesn't satisfy TS here
        children: createElement(Bomb),
      }),
    )
    expect(screen.getByText('fallback')).toBeTruthy()

    // DetailPane keys the boundary on the request id — a different id is a different
    // React key, which remounts the boundary fresh rather than reusing crashed state.
    rerender(
      createElement(RenderErrorBoundary, {
        key: 'request-2',
        fallback: createElement('div', null, 'fallback'),
        // eslint-disable-next-line react/no-children-prop -- createElement's 3-arg overload doesn't satisfy TS here
        children: createElement('div', null, 'a different, valid capture'),
      }),
    )

    expect(screen.getByText('a different, valid capture')).toBeTruthy()
    expect(screen.queryByText('fallback')).toBeNull()
    consoleError.mockRestore()
  })
})
