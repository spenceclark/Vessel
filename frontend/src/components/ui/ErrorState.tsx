import { Button } from '@/components/ui/button'

/**
 * D8 (review §4 risk) — a failed query used to fall through to whatever "loading" or
 * "empty" branch happened to run next, so a genuine backend/network failure looked
 * identical to "still loading" (forever) or "nothing here" (misleadingly). One shared
 * shape for "this failed, and here's how to try again" wherever a query can fail:
 * config, request detail, and the request list's own initial load.
 */
export function ErrorState({ message, onRetry }: { message: string; onRetry: () => void }) {
  return (
    <div className="flex h-full flex-col items-center justify-center gap-2 p-6 text-center">
      <p className="text-sm text-danger">{message}</p>
      <Button onClick={onRetry}>Retry</Button>
    </div>
  )
}
