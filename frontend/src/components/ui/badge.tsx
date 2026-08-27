import * as React from 'react'
import { cva, type VariantProps } from 'class-variance-authority'
import { cn } from '@/lib/utils'

const badgeVariants = cva(
  'inline-flex items-center gap-1 rounded-full border px-2 py-0.5 text-xs font-medium leading-none',
  {
    variants: {
      variant: {
        default: 'border-[var(--border)] bg-[var(--card)] text-[var(--foreground)]',
        outline: 'border-[var(--border)] bg-transparent text-[var(--muted)]',
        accent: 'border-transparent bg-[var(--accent)]/15 text-[var(--accent)]',
        warning: 'border-transparent bg-[var(--warning)]/15 text-[var(--warning)]',
        danger: 'border-transparent bg-[var(--danger)]/15 text-[var(--danger)]',
      },
    },
    defaultVariants: { variant: 'default' },
  },
)

export interface BadgeProps
  extends React.HTMLAttributes<HTMLSpanElement>,
    VariantProps<typeof badgeVariants> {}

export function Badge({ className, variant, ...props }: BadgeProps) {
  return <span className={cn(badgeVariants({ variant }), className)} {...props} />
}
