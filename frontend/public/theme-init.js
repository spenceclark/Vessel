// Applies a persisted Light/Dark choice before first paint, avoiding a flash of the wrong
// theme; src/lib/theme.ts's useTheme takes over from here. "system" (the default, nothing
// stored) intentionally leaves no attribute — see index.css. A same-origin file, not an
// inline <script>, so it isn't blocked by the CSP the embedded UI serves itself under
// (script-src 'self', no 'unsafe-inline' — see VesselApp.cs).
;(function () {
  try {
    var theme = localStorage.getItem('vessel-theme')
    if (theme === 'light' || theme === 'dark') {
      document.documentElement.setAttribute('data-theme', theme)
    }
  } catch {}
})()
