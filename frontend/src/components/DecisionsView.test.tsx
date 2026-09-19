import { cleanup, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it } from 'vitest'
import type { RequestDetail } from '@/api/types'
import { renderRequest, renderResponse } from '@/render'
import { MessageView } from './MessageView'

/** #113 — `MessageView` hands a `typesafe-systemone` view to `DecisionsView`, end to end from wire JSON. */

afterEach(cleanup)

const detail = {
  format: 'typesafe-systemone',
  requestBody: {
    text: JSON.stringify({
      state: 'You have charged me twice.',
      questions: {
        area: { type: 'choice', instructions: { question: 'Which team owns it?' }, criteria: { billing: 'Payments', bug: null } },
        odd: { type: 'rank', instructions: 'Rank them.' },
      },
    }),
  },
  responseBody: {
    text: JSON.stringify({
      answers: {
        area: { type: 'choice', choice: 'billing', probabilities: { bug: 0.25, billing: 0.75 }, confidence: 0.6 },
        odd: { type: 'rank', order: ['a', 'b'] },
      },
    }),
  },
} as RequestDetail

describe('DecisionsView', () => {
  it('renders request cards without an answer section', () => {
    render(<MessageView view={renderRequest(detail)!} />)
    expect(screen.getByText('You have charged me twice.')).toBeTruthy()
    expect(screen.getByText('Which team owns it?')).toBeTruthy()
    expect(screen.getByText('Payments')).toBeTruthy()
    expect(screen.queryByRole('meter')).toBeNull()
  })

  it('renders probability bars sorted with the winner first, confidence, and unknown answers as JSON', () => {
    render(<MessageView view={renderResponse(detail)!} />)
    const meters = screen.getAllByRole('meter')
    expect(meters.map((m) => [m.getAttribute('aria-label'), m.getAttribute('aria-valuenow')])).toEqual([['billing', '75'], ['bug', '25']])
    expect(screen.getByText('confidence 0.6')).toBeTruthy()
    expect(screen.getByText(/"order"/)).toBeTruthy()
  })
})
