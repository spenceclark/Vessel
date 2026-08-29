import { useCallback, useEffect, useState } from 'react'

/**
 * Settings-menu appearance control. `system` (the default) applies no `data-theme`
 * attribute at all, so `index.css`'s `prefers-color-scheme` media query decides;
 * `light`/`dark` set the attribute on `<html>`, which `index.css` gives higher
 * specificity than the media query so an explicit choice always wins over the OS.
 */
export type ThemePreference = 'light' | 'dark' | 'system'

/** Kept in sync by hand with the inline script in index.html (plain JS, can't import this). */
export const THEME_STORAGE_KEY = 'vessel-theme'

function isThemePreference(value: string | null): value is ThemePreference {
  return value === 'light' || value === 'dark' || value === 'system'
}

/** Reads the persisted choice, defaulting to `system` — including when storage is unavailable. */
export function loadTheme(): ThemePreference {
  try {
    const stored = localStorage.getItem(THEME_STORAGE_KEY)
    return isThemePreference(stored) ? stored : 'system'
  } catch {
    return 'system'
  }
}

/** Sets (or clears, for `system`) the `data-theme` attribute index.css reads. */
export function applyTheme(theme: ThemePreference) {
  if (theme === 'system') {
    delete document.documentElement.dataset.theme
  } else {
    document.documentElement.dataset.theme = theme
  }
}

/**
 * The settings-menu theme control's state. Applies (and persists) on every change,
 * and once more on mount so a value already applied by index.html's inline
 * before-paint script (which reads the same storage key) stays in sync with React
 * state — that script exists purely to avoid a flash of the wrong theme; this hook
 * owns the value from here on.
 */
export function useTheme(): [ThemePreference, (theme: ThemePreference) => void] {
  const [theme, setThemeState] = useState<ThemePreference>(loadTheme)

  useEffect(() => {
    applyTheme(theme)
  }, [theme])

  const setTheme = useCallback((next: ThemePreference) => {
    setThemeState(next)
    try {
      if (next === 'system') localStorage.removeItem(THEME_STORAGE_KEY)
      else localStorage.setItem(THEME_STORAGE_KEY, next)
    } catch {
      // Storage may be unavailable (private mode, disabled) — the in-memory choice
      // still applies for the rest of this tab's session, it just won't persist.
    }
  }, [])

  return [theme, setTheme]
}
