import { clsx, type ClassValue } from 'clsx'
import { extendTailwindMerge } from 'tailwind-merge'

/**
 * ui-spec.md §9.1 — plain `twMerge` only knows Tailwind's own font-size scale
 * (xs/sm/base/lg/...), so the app's custom `text-stat` utility (`--text-stat` in
 * index.css's `@theme`) reads to it as an unrecognized `text-{value}` and falls into the
 * text-*color* group instead — a trailing color utility like `text-text`/`text-danger`
 * then "wins" the (wrong) conflict and silently drops `text-stat`, so every `Stat` value
 * rendered at the `sm` size instead of `stat`. Registering `stat` in the `font-size`
 * group fixes the classification. Audited the app's other custom `@theme` utilities for
 * the same hazard: `rounded-panel/control/chip` and `shadow-panel/dialog` use prefixes
 * (`rounded-`, `shadow-`) with no competing same-prefix semantic group, so they don't
 * collide the way a `text-*` size name does with `text-*` colors.
 */
const twMerge = extendTailwindMerge({
  extend: {
    classGroups: {
      'font-size': [{ text: ['stat'] }],
    },
  },
})

export function cn(...inputs: ClassValue[]) {
  return twMerge(clsx(inputs))
}
