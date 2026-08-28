import * as React from 'react'
import { cva, type VariantProps } from 'class-variance-authority'
import { cn } from '@/lib/utils'

/**
 * §6 — one height (28px = h-7) plus a 24px icon button, no size zoo. `default` is the
 * neutral filled look; `primary` is accent-filled — one per view, max (audited: only
 * ConfigPanel's Save qualifies, everywhere else uses default/destructive/ghost).
 */
const buttonVariants = cva(
  'inline-flex items-center justify-center gap-1.5 whitespace-nowrap rounded-control text-sm font-medium transition-colors disabled:pointer-events-none disabled:opacity-50',
  {
    variants: {
      variant: {
        default: 'border border-border bg-surface-2 text-text hover:bg-surface-3',
        primary: 'bg-accent text-accent-fg hover:opacity-90',
        destructive:
          'bg-[color-mix(in_srgb,var(--color-danger)_14%,transparent)] text-danger hover:bg-[color-mix(in_srgb,var(--color-danger)_20%,transparent)]',
        ghost: 'bg-transparent text-text-secondary hover:bg-surface-2 hover:text-text',
      },
      size: {
        default: 'h-7 px-3',
        icon: 'h-6 w-6',
      },
    },
    defaultVariants: {
      variant: 'default',
      size: 'default',
    },
  },
)

export interface ButtonProps
  extends React.ButtonHTMLAttributes<HTMLButtonElement>,
    VariantProps<typeof buttonVariants> {}

export function Button({ className, variant, size, ...props }: ButtonProps) {
  return <button className={cn(buttonVariants({ variant, size }), className)} {...props} />
}
