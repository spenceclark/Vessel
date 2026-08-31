import { useCallback, useEffect, useId, useRef, useState, type ReactNode } from 'react'
import { cn } from '@/lib/utils'

/** A compact, anchored disclosure surface for overflow detail. */
export function Popover({
  trigger,
  children,
  contentClassName,
  label,
  onOpenChange,
}: {
  trigger: (open: boolean, toggle: () => void, contentId: string) => ReactNode
  children?: ReactNode | ((close: () => void) => ReactNode)
  contentClassName?: string
  label: string
  onOpenChange?: (open: boolean) => void
}) {
  const [open, setOpen] = useState(false)
  const rootRef = useRef<HTMLDivElement>(null)
  const contentId = useId()

  const changeOpen = useCallback((next: boolean) => {
    setOpen(next)
    onOpenChange?.(next)
  }, [onOpenChange])

  useEffect(() => {
    if (!open) return

    function closeOnOutsidePointerDown(event: PointerEvent) {
      if (!rootRef.current?.contains(event.target as Node)) changeOpen(false)
    }

    function closeOnEscape(event: KeyboardEvent) {
      if (event.key === 'Escape') changeOpen(false)
    }

    document.addEventListener('pointerdown', closeOnOutsidePointerDown)
    document.addEventListener('keydown', closeOnEscape)
    return () => {
      document.removeEventListener('pointerdown', closeOnOutsidePointerDown)
      document.removeEventListener('keydown', closeOnEscape)
    }
  }, [open, changeOpen])

  return (
    <div ref={rootRef} className="relative">
      {trigger(open, () => changeOpen(!open), contentId)}
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
          {typeof children === 'function' ? children(() => changeOpen(false)) : children}
        </div>
      )}
    </div>
  )
}
