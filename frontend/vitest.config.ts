import { defineConfig } from 'vitest/config'
import path from 'node:path'

/**
 * Kept separate from vite.config.ts on purpose. Vitest bundles its own Vite (rollup-based)
 * while the app builds on Vite 8 (rolldown-based); merging the two configs makes their
 * plugin types structurally incompatible and breaks `tsc -b` for the app. The tests need
 * none of the app's build plugins — only the `@` alias and a DOM — so keeping the surfaces
 * apart is both simpler and what keeps the production build honest.
 *
 * R10/R11 — the live-history reconciliation is the one piece of frontend logic whose
 * failure modes (a completion lost across a fetch boundary; an in-flight row that never
 * clears) are invisible to the type checker and only reproducible under real timing, so it
 * is the one place a component-level harness earns its keep.
 */
export default defineConfig({
  resolve: {
    alias: {
      '@': path.resolve(import.meta.dirname, 'src'),
    },
  },
  test: {
    environment: 'jsdom',
    include: ['src/**/*.test.{ts,tsx}'],
    restoreMocks: true,
  },
})
