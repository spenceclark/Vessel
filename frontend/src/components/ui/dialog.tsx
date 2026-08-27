import type { ReactNode } from 'react'
import { cn } from '@/lib/utils'

/** D6/D7 — a general-purpose modal shell for the Data/Config panels: title, close, arbitrary content. */
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
  if (!open) return null

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40" role="presentation" onClick={onClose}>
      <div
        role="dialog"
        aria-modal="true"
        aria-labelledby="dialog-title"
        className={cn(
          'flex max-h-[85vh] flex-col rounded-lg border border-[var(--border)] bg-[var(--background)] shadow-lg',
          widthClassName ?? 'w-[560px]',
        )}
        onClick={(e) => e.stopPropagation()}
      >
        <div className="flex shrink-0 items-center justify-between border-b border-[var(--border)] px-4 py-2.5">
          <h2 id="dialog-title" className="text-sm font-semibold text-[var(--foreground)]">
            {title}
          </h2>
          <button
            type="button"
            onClick={onClose}
            aria-label="Close"
            className="rounded px-1.5 py-0.5 text-[var(--muted)] hover:bg-[var(--card)] hover:text-[var(--foreground)]"
          >
            ✕
          </button>
        </div>
        <div className="min-h-0 flex-1 overflow-y-auto p-4">{children}</div>
      </div>
    </div>
  )
}

/** A minimal confirm-style modal — just enough for "Reset session?" (D6). */
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
  if (!open) return null

  return (
    <div
      className="fixed inset-0 z-50 flex items-center justify-center bg-black/40"
      role="presentation"
      onClick={onCancel}
    >
      <div
        role="alertdialog"
        aria-modal="true"
        aria-labelledby="confirm-dialog-title"
        className="w-80 rounded-lg border border-[var(--border)] bg-[var(--background)] p-4 shadow-lg"
        onClick={(e) => e.stopPropagation()}
      >
        <h2 id="confirm-dialog-title" className="text-sm font-semibold text-[var(--foreground)]">
          {title}
        </h2>
        {description && <p className="mt-1 text-sm text-[var(--muted)]">{description}</p>}
        <div className="mt-4 flex justify-end gap-2">
          <button
            type="button"
            onClick={onCancel}
            className="h-7 rounded-md px-3 text-sm text-[var(--muted)] hover:bg-[var(--card)]"
          >
            Cancel
          </button>
          <button
            type="button"
            onClick={onConfirm}
            className="h-7 rounded-md bg-[var(--accent)] px-3 text-sm text-white hover:opacity-90"
          >
            {confirmLabel}
          </button>
        </div>
      </div>
    </div>
  )
}
