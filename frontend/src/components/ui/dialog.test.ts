import { createElement } from 'react'
import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { ConfirmDialog, Dialog } from './dialog'

describe('Dialog', () => {
  afterEach(cleanup)

  it('ignores backdrop clicks but closes from its close button and Escape', () => {
    const onClose = vi.fn()
    // oxlint-disable-next-line react/no-children-prop -- this .ts test intentionally uses createElement rather than JSX.
    render(createElement(Dialog, { open: true, title: 'Settings', onClose, children: 'Contents' }))

    fireEvent.click(screen.getByRole('presentation'))
    expect(onClose).not.toHaveBeenCalled()

    fireEvent.click(screen.getByRole('button', { name: 'Close' }))
    expect(onClose).toHaveBeenCalledOnce()

    fireEvent.keyDown(document, { key: 'Escape' })
    expect(onClose).toHaveBeenCalledTimes(2)
  })

  it('ignores backdrop clicks for confirmations but allows Escape and Cancel', () => {
    const onCancel = vi.fn()
    render(createElement(ConfirmDialog, { open: true, title: 'Reset?', onConfirm: vi.fn(), onCancel }))

    fireEvent.click(screen.getByRole('presentation'))
    expect(onCancel).not.toHaveBeenCalled()

    fireEvent.keyDown(document, { key: 'Escape' })
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }))
    expect(onCancel).toHaveBeenCalledTimes(2)
  })
})
