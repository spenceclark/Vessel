import { useEffect, useRef, type ReactNode, type RefObject } from 'react'
import { cn } from '@/lib/utils'

const FOCUSABLE = 'button, [href], input, select, textarea, [tabindex]:not([tabindex="-1"])'

/**
 * §8.7 — Escape-to-close + a basic focus trap, shared by Dialog and ConfirmDialog.
 * Focuses the first focusable element on open, cycles Tab/Shift+Tab within the dialog,
 * and restores focus to whatever had it beforehand on close.
 */
function useDialogA11y(open: boolean, onClose: () => void, ref: RefObject<HTMLElement | null>) {
  useEffect(() => {
    if (!open) return

    const container = ref.current
    const previouslyFocused = document.activeElement as HTMLElement | null
    container?.querySelector<HTMLElement>(FOCUSABLE)?.focus()

    function onKeyDown(e: KeyboardEvent) {
      if (e.key === 'Escape') {
        e.preventDefault()
        onClose()
        return
      }

      if (e.key !== 'Tab' || !container) return
      const items = Array.from(container.querySelectorAll<HTMLElement>(FOCUSABLE)).filter((el) => !el.hasAttribute('disabled'))
      if (items.length === 0) return
      const first = items[0]
      const last = items[items.length - 1]

      if (e.shiftKey && document.activeElement === first) {
        e.preventDefault()
        last.focus()
      } else if (!e.shiftKey && document.activeElement === last) {
        e.preventDefault()
        first.focus()
      }
    }

    document.addEventListener('keydown', onKeyDown)
    return () => {
      document.removeEventListener('keydown', onKeyDown)
      previouslyFocused?.focus()
    }
  }, [open, onClose, ref])
}

/** §6 — radius-panel, surface, shadow-dialog, 420px default width, 100ms fade. */
export function Dialog({
  open,
  title,
  onClose,
  children,
  widthClassName,
}: {
  open: boolean
  title: string
  onClose: () => void
  children: ReactNode
  widthClassName?: string
}) {
  const containerRef = useRef<HTMLDivElement>(null)
  useDialogA11y(open, onClose, containerRef)

  if (!open) return null

  return (
    <div
      className="dialog-fade fixed inset-0 z-50 flex items-center justify-center bg-black/50"
      role="presentation"
      onClick={onClose}
    >
      <div
        ref={containerRef}
        role="dialog"
        aria-modal="true"
        aria-labelledby="dialog-title"
        className={cn(
          'flex max-h-[85vh] flex-col rounded-panel border border-border bg-surface shadow-dialog',
          widthClassName ?? 'w-[420px]',
        )}
        onClick={(e) => e.stopPropagation()}
      >
        <div className="flex shrink-0 items-center justify-between border-b border-border px-4 py-2.5">
          <h2 id="dialog-title" className="text-lg font-medium text-text">
            {title}
          </h2>
          <button
            type="button"
            onClick={onClose}
            aria-label="Close"
            className="rounded-control px-1.5 py-0.5 text-text-muted hover:bg-surface-2 hover:text-text"
          >
            ✕
          </button>
        </div>
        <div className="min-h-0 flex-1 overflow-y-auto p-4">{children}</div>
      </div>
    </div>
  )
}

/** A minimal confirm-style modal — just enough for "Reset session?" and destructive confirmations (§6). */
export function ConfirmDialog({
  open,
  title,
  description,
  confirmLabel = 'Confirm',
  onConfirm,
  onCancel,
}: {
  open: boolean
  title: string
  description?: string
  confirmLabel?: string
  onConfirm: () => void
  onCancel: () => void
}) {
  const containerRef = useRef<HTMLDivElement>(null)
  useDialogA11y(open, onCancel, containerRef)

  if (!open) return null

  return (
    <div
      className="dialog-fade fixed inset-0 z-50 flex items-center justify-center bg-black/50"
      role="presentation"
      onClick={onCancel}
    >
      <div
        ref={containerRef}
        role="alertdialog"
        aria-modal="true"
        aria-labelledby="confirm-dialog-title"
        className="w-80 rounded-panel border border-border bg-surface p-4 shadow-dialog"
        onClick={(e) => e.stopPropagation()}
      >
        <h2 id="confirm-dialog-title" className="text-lg font-medium text-text">
          {title}
        </h2>
        {description && <p className="mt-1 text-sm text-text-secondary">{description}</p>}
        <div className="mt-4 flex justify-end gap-2">
          <button
            type="button"
            onClick={onCancel}
            className="h-7 rounded-control px-3 text-sm text-text-secondary hover:bg-surface-2"
          >
            Cancel
          </button>
          <button
            type="button"
            onClick={onConfirm}
            className="h-7 rounded-control bg-accent px-3 text-sm text-accent-fg hover:opacity-90"
          >
            {confirmLabel}
          </button>
        </div>
      </div>
    </div>
  )
}
