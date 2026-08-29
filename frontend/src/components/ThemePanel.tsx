import { Monitor, Moon, Sun } from 'lucide-react'
import { useTheme, type ThemePreference } from '@/lib/theme'
import { Tabs, TabsList, TabsTrigger } from '@/components/ui/tabs'

const OPTIONS: { value: ThemePreference; label: string; Icon: typeof Sun }[] = [
  { value: 'light', label: 'Light', Icon: Sun },
  { value: 'dark', label: 'Dark', Icon: Moon },
  { value: 'system', label: 'System', Icon: Monitor },
]

/** Settings-menu appearance control — Light/Dark/System, the same segmented-control idiom as every other tri-state choice in this app (§6). */
export function ThemePanel() {
  const [theme, setTheme] = useTheme()

  return (
    <div className="flex flex-col gap-2">
      <span className="text-xs font-[550] uppercase tracking-[0.06em] text-text-muted">Theme</span>
      <Tabs value={theme} onValueChange={(v) => setTheme(v as ThemePreference)}>
        <TabsList>
          {OPTIONS.map(({ value, label, Icon }) => (
            <TabsTrigger key={value} value={value} className="inline-flex items-center gap-1.5">
              <Icon className="h-3.5 w-3.5" strokeWidth={1.75} />
              {label}
            </TabsTrigger>
          ))}
        </TabsList>
      </Tabs>
      <p className="text-xs text-text-muted">
        "System" follows your OS's light/dark setting; Light and Dark stay fixed regardless of it.
      </p>
    </div>
  )
}
