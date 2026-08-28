import * as React from 'react'
import { cn } from '@/lib/utils'

/** §6 — surface-2 fill, border, radius-control, 28px, sm; optional leading icon (search inputs). */
export interface InputProps extends React.InputHTMLAttributes<HTMLInputElement> {
  icon?: React.ReactNode
}

export const Input = React.forwardRef<HTMLInputElement, InputProps>(function Input({ className, icon, ...props }, ref) {
  const control = (
    <input
      ref={ref}
      className={cn(
        'h-7 w-full rounded-control border border-border bg-surface-2 text-sm text-text placeholder:text-text-muted',
        icon ? 'pl-7 pr-2' : 'px-2',
        className,
      )}
      {...props}
    />
  )

  if (!icon) return control

  return (
    <div className="relative">
      <span className="pointer-events-none absolute left-2 top-1/2 -translate-y-1/2 text-text-muted [&>svg]:h-3.5 [&>svg]:w-3.5">
        {icon}
      </span>
      {control}
    </div>
  )
})
