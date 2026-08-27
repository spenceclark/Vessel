import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'
import path from 'node:path'

// D1: base matches the fixed /vessel/ mount point the embedded build is served from, so
// dev-mode asset resolution matches production. The dev proxy forwards API + SSE calls to
// a locally running Vessel instance — same-origin from the page's point of view, so there's
// no CORS anywhere.
export default defineConfig({
  base: '/vessel/',
  plugins: [react(), tailwindcss()],
  resolve: {
    alias: {
      '@': path.resolve(import.meta.dirname, 'src'),
    },
  },
  server: {
    proxy: {
      '/vessel/api': {
        target: 'http://127.0.0.1:4550',
        changeOrigin: true,
      },
    },
  },
})
