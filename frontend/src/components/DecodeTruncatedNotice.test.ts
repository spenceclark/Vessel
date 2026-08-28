import { createElement } from 'react'
import { render, screen, cleanup } from '@testing-library/react'
import { afterEach, describe, expect, it } from 'vitest'
import { DecodeTruncatedNotice } from './DecodeTruncatedNotice'

/**
 * R05 remainder — the review's exact reproduction: lowering `capture.maxBodyMb` after a
 * body was already captured makes `GET /requests/{id}` return a `decodeTruncated: true`
 * prefix with no frontend indication. This pins that the notice appears whenever the flag
 * is set, stays silent otherwise, and is distinguishable from capture-time truncation
 * (which has its own separate "Truncated"/`body_truncated` badge, not this component).
 */

afterEach(cleanup)

describe('DecodeTruncatedNotice', () => {
  it('renders a warning when the body is a decode-truncated prefix', () => {
    render(createElement(DecodeTruncatedNotice, { body: { text: 'partial body', decodeTruncated: true } }))
    expect(screen.getByRole('alert')).toBeTruthy()
    expect(screen.getByText(/display decode limit reached/i)).toBeTruthy()
    expect(screen.getByText(/capture\.maxBodyMb/)).toBeTruthy()
  })

  it('renders nothing for an untruncated body', () => {
    render(createElement(DecodeTruncatedNotice, { body: { text: 'complete body' } }))
    expect(screen.queryByRole('alert')).toBeNull()
  })

  it('renders nothing for an explicitly false flag or a missing body', () => {
    render(createElement(DecodeTruncatedNotice, { body: { text: 'x', decodeTruncated: false } }))
    expect(screen.queryByRole('alert')).toBeNull()
    cleanup()
    render(createElement(DecodeTruncatedNotice, { body: null }))
    expect(screen.queryByRole('alert')).toBeNull()
  })

  it('reports the shown byte length, not the base64 character length', () => {
    // 4 base64 chars decode to exactly 3 bytes.
    render(createElement(DecodeTruncatedNotice, { body: { base64: 'QUJD', decodeTruncated: true } }))
    expect(screen.getByText(/3 B/)).toBeTruthy()
  })
})
