import { createElement } from 'react'
import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it } from 'vitest'
import { Popover } from './popover'

describe('Popover', () => {
  afterEach(cleanup)

  it('toggles from its trigger and dismisses on outside click or Escape', () => {
    render(
      createElement(Popover, {
        label: 'Overflow details',
        trigger: (open, toggle, contentId) => createElement('button', { onClick: toggle, 'aria-controls': contentId }, open ? 'Close' : 'Open'),
      }, 'Overflow details'),
    )

    fireEvent.click(screen.getByRole('button', { name: 'Open' }))
    expect(screen.getByRole('group', { name: 'Overflow details' }).textContent).toContain('Overflow details')

    fireEvent.pointerDown(document.body)
    expect(screen.queryByRole('group', { name: 'Overflow details' })).toBeNull()

    fireEvent.click(screen.getByRole('button', { name: 'Open' }))
    fireEvent.keyDown(document, { key: 'Escape' })
    expect(screen.queryByRole('group', { name: 'Overflow details' })).toBeNull()
  })
})
