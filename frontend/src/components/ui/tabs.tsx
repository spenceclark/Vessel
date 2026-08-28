import * as React from 'react'
import { cn } from '@/lib/utils'

interface TabsContextValue {
  value: string
  setValue: (value: string) => void
}

const TabsContext = React.createContext<TabsContextValue | null>(null)

export function Tabs({
  value,
  onValueChange,
  className,
  children,
}: {
  value: string
  onValueChange: (value: string) => void
  className?: string
  children: React.ReactNode
}) {
  return (
    <TabsContext.Provider value={{ value, setValue: onValueChange }}>
      <div className={className}>{children}</div>
    </TabsContext.Provider>
  )
}

/** §6 — segmented control: a surface-2 track, no underline tabs anywhere. */
export function TabsList({ className, children }: { className?: string; children: React.ReactNode }) {
  return (
    <div className={cn('inline-flex items-center gap-0.5 rounded-control bg-surface-2 p-0.5', className)} role="tablist">
      {children}
    </div>
  )
}

export function TabsTrigger({
  value,
  children,
  className,
  disabled,
}: {
  value: string
  children: React.ReactNode
  className?: string
  disabled?: boolean
}) {
  const ctx = React.useContext(TabsContext)
  if (!ctx) throw new Error('TabsTrigger must be used inside Tabs')
  const active = ctx.value === value

  return (
    <button
      type="button"
      role="tab"
      aria-selected={active}
      disabled={disabled}
      onClick={() => ctx.setValue(value)}
      className={cn(
        'rounded-chip px-3 py-1 text-sm font-medium transition-colors disabled:pointer-events-none disabled:opacity-50',
        active
          ? 'border border-border bg-surface text-text'
          : 'border border-transparent text-text-secondary hover:text-text',
        className,
      )}
    >
      {children}
    </button>
  )
}

export function TabsContent({
  value,
  children,
  className,
}: {
  value: string
  children: React.ReactNode
  className?: string
}) {
  const ctx = React.useContext(TabsContext)
  if (!ctx) throw new Error('TabsContent must be used inside Tabs')
  if (ctx.value !== value) return null
  return <div className={className}>{children}</div>
}
