import { useEffect, useId, useRef, useState, type ReactNode } from 'react'
import { cn } from '@/lib/utils'

/** A compact, anchored disclosure surface for overflow detail. */
export function Popover({
  trigger,
  children,
  contentClassName,
  label,
}: {
  trigger: (open: boolean, toggle: () => void, contentId: string) => ReactNode
  children?: ReactNode
  contentClassName?: string
  label: string
}) {
  const [open, setOpen] = useState(false)
  const rootRef = useRef<HTMLDivElement>(null)
  const contentId = useId()

  useEffect(() => {
    if (!open) return

    function closeOnOutsidePointerDown(event: PointerEvent) {
      if (!rootRef.current?.contains(event.target as Node)) setOpen(false)
    }

    function closeOnEscape(event: KeyboardEvent) {
      if (event.key === 'Escape') setOpen(false)
    }

    document.addEventListener('pointerdown', closeOnOutsidePointerDown)
    document.addEventListener('keydown', closeOnEscape)
    return () => {
      document.removeEventListener('pointerdown', closeOnOutsidePointerDown)
      document.removeEventListener('keydown', closeOnEscape)
    }
  }, [open])

  return (
    <div ref={rootRef} className="relative">
      {trigger(open, () => setOpen((value) => !value), contentId)}
      {open && (
        <div
          id={contentId}
          role="group"
          aria-label={label}
          className={cn(
            'absolute right-0 top-full z-40 mt-2 w-64 rounded-control border border-border bg-surface p-2 shadow-panel',
            contentClassName,
          )}
        >
          {children}
        </div>
      )}
    </div>
  )
}
