import * as React from 'react'
import { cva, type VariantProps } from 'class-variance-authority'
import { cn } from '@/lib/utils'

/**
 * §6 — pill-shaped (9999px), xs, 4×10 padding, 10-14% tinted fill + colored text.
 * Vocabulary: warnings → warn, errors → danger, info-class (tokens_estimated,
 * usage_injected, redacted markers) → info, format/method/image markers → neutral.
 * Tags pick one of 5 hues via a hash of the tag string (`lib/tags.ts`) so the same
 * name always renders the same color — categorical, not status, so these hues are
 * kept distinct from the semantic trio + accent above. Selectable toggle chips
 * (FilterBar's tag picker) don't use this vocabulary at all when *selected* — accent
 * is for interaction/identity, not data (§2.2), so that state is styled directly
 * rather than through a Badge variant.
 */
const badgeVariants = cva('inline-flex items-center gap-1 rounded-full px-2.5 py-1 text-xs font-medium leading-none', {
  variants: {
    variant: {
      neutral: 'bg-surface-2 text-text-secondary',
      warn: 'bg-[color-mix(in_srgb,var(--color-warn)_14%,transparent)] text-warn',
      danger: 'bg-[color-mix(in_srgb,var(--color-danger)_14%,transparent)] text-danger',
      info: 'bg-[color-mix(in_srgb,var(--color-info)_14%,transparent)] text-info',
      'tag-blue': 'bg-[color-mix(in_srgb,var(--color-tag-blue)_14%,transparent)] text-tag-blue',
      'tag-indigo': 'bg-[color-mix(in_srgb,var(--color-tag-indigo)_14%,transparent)] text-tag-indigo',
      'tag-violet': 'bg-[color-mix(in_srgb,var(--color-tag-violet)_14%,transparent)] text-tag-violet',
      'tag-pink': 'bg-[color-mix(in_srgb,var(--color-tag-pink)_14%,transparent)] text-tag-pink',
      'tag-fuchsia': 'bg-[color-mix(in_srgb,var(--color-tag-fuchsia)_14%,transparent)] text-tag-fuchsia',
      'tag-steel': 'bg-[color-mix(in_srgb,var(--color-tag-steel)_14%,transparent)] text-tag-steel',
    },
  },
  defaultVariants: { variant: 'neutral' },
})

export interface BadgeProps
  extends React.HTMLAttributes<HTMLSpanElement>,
    VariantProps<typeof badgeVariants> {}

export function Badge({ className, variant, ...props }: BadgeProps) {
  return <span className={cn(badgeVariants({ variant }), className)} {...props} />
}
