import { Component, type ReactNode } from 'react'

/**
 * R17 — `render/validate.ts` rejects the specific malformed-view shapes the review found,
 * but it can't be a proof that no captured JSON can ever reach `MessageView` in a way
 * that throws during render (a future extractor bug, a `ReactMarkdown` edge case, …). A
 * per-tab boundary is the backstop: one bad capture's rendered view crashing degrades to
 * that tab's raw-JSON fallback, exactly what an extraction failure already does — it
 * never takes the rest of the app down, and there's nothing above `main.tsx` to catch it
 * otherwise. Keyed by the caller on the request id, so navigating to a different capture
 * always gets a fresh boundary rather than staying stuck showing the previous one's error.
 */
export class RenderErrorBoundary extends Component<{ children: ReactNode; fallback: ReactNode }, { hasError: boolean }> {
  state = { hasError: false }

  static getDerivedStateFromError() {
    return { hasError: true }
  }

  componentDidCatch(error: unknown) {
    console.error('Vessel: rendered view crashed, falling back to raw JSON', error)
  }

  render() {
    return this.state.hasError ? this.props.fallback : this.props.children
  }
}
