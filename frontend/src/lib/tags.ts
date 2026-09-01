export const TAG_VARIANTS = ['tag-blue', 'tag-indigo', 'tag-violet', 'tag-pink', 'tag-fuchsia', 'tag-steel'] as const

export type TagVariant = (typeof TAG_VARIANTS)[number]

/** Deterministic hash so the same tag string always picks the same color (djb2). */
export function tagVariant(tag: string): TagVariant {
  let hash = 5381
  for (let i = 0; i < tag.length; i++) {
    hash = (hash * 33) ^ tag.charCodeAt(i)
  }
  return TAG_VARIANTS[Math.abs(hash) % TAG_VARIANTS.length]
}

/** The ramp index (0-based) a tag's pill hash picks — the same index §2.3's --chart-N uses. */
export function tagVariantIndex(tag: string): number {
  return TAG_VARIANTS.indexOf(tagVariant(tag))
}

// Same tint-fill + colored-text look as the Badge `tag-*` variants (ui/badge.tsx),
// for the one place a tag renders outside a Badge: FilterBar's unselected chips.
const TAG_CHIP_CLASS: Record<TagVariant, string> = {
  'tag-blue': 'bg-[color-mix(in_srgb,var(--color-tag-blue)_14%,transparent)] text-tag-blue',
  'tag-indigo': 'bg-[color-mix(in_srgb,var(--color-tag-indigo)_14%,transparent)] text-tag-indigo',
  'tag-violet': 'bg-[color-mix(in_srgb,var(--color-tag-violet)_14%,transparent)] text-tag-violet',
  'tag-pink': 'bg-[color-mix(in_srgb,var(--color-tag-pink)_14%,transparent)] text-tag-pink',
  'tag-fuchsia': 'bg-[color-mix(in_srgb,var(--color-tag-fuchsia)_14%,transparent)] text-tag-fuchsia',
  'tag-steel': 'bg-[color-mix(in_srgb,var(--color-tag-steel)_14%,transparent)] text-tag-steel',
}

export function tagChipClass(tag: string): string {
  return TAG_CHIP_CLASS[tagVariant(tag)]
}
