/**
 * §1.2 — the code-drawn mark: a rounded-rectangle vessel with a flow line passing
 * through it (traffic passing through, observed). No image assets — this doubles as
 * the header logo and every empty-state icon; `public/favicon.svg` is a static render
 * of the same geometry with hardcoded colors (favicons don't reliably inherit page
 * custom properties).
 */
export function Mark({ size = 20, muted = false, className }: { size?: number; muted?: boolean; className?: string }) {
  return (
    <svg
      width={size}
      height={size}
      viewBox="0 0 24 24"
      fill="none"
      xmlns="http://www.w3.org/2000/svg"
      className={className}
      style={{ opacity: muted ? 0.5 : 1 }}
      aria-hidden="true"
    >
      <rect x="4" y="5" width="16" height="14" rx="4" stroke="var(--color-text-muted)" strokeWidth="2" />
      <line x1="0" y1="12" x2="4" y2="12" stroke="var(--color-text-muted)" strokeWidth="2" strokeLinecap="round" />
      <line x1="20" y1="12" x2="24" y2="12" stroke="var(--color-text-muted)" strokeWidth="2" strokeLinecap="round" />
      <line x1="7" y1="12" x2="17" y2="12" stroke="var(--color-accent)" strokeWidth="2" strokeLinecap="round" />
      <circle cx="12" cy="12" r="1" fill="var(--color-accent)" />
    </svg>
  )
}
