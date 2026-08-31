import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Download } from 'lucide-react'
import { api } from '@/api/client'
import type { ExportBodies, ExportFormat, RequestFilters, SessionScope } from '@/api/types'
import { Button } from '@/components/ui/button'
import { Popover } from '@/components/ui/popover'
import { cn } from '@/lib/utils'

const SELECT_CLASS = 'h-7 w-full rounded-control border border-border bg-surface-2 px-1.5 text-xs text-text'

/** #24 — format/body choices plus the exact filtered row count before download. */
export function ExportControl({
  scope,
  filters,
}: {
  scope: SessionScope | null
  filters: RequestFilters
}) {
  const [open, setOpen] = useState(false)
  const [format, setFormat] = useState<ExportFormat>('jsonl')
  const [bodies, setBodies] = useState<ExportBodies>('none')
  const countQuery = useQuery({
    queryKey: ['export-count', scope, filters],
    queryFn: () => api.getExportCount(scope!, filters),
    enabled: open && scope !== null,
  })

  function changeFormat(next: ExportFormat) {
    setFormat(next)
    if (next === 'csv' && bodies === 'full') setBodies('text')
  }

  const count = countQuery.data?.count
  const href = scope === null ? undefined : api.exportUrl(scope, filters, format, bodies)
  const canDownload = href !== undefined && !countQuery.isPending && !countQuery.isError

  return (
    <Popover
      label="Export requests"
      onOpenChange={setOpen}
      contentClassName="w-72 p-3"
      trigger={(isOpen, toggle, contentId) => (
        <Button
          type="button"
          variant="default"
          onClick={toggle}
          aria-expanded={isOpen}
          aria-controls={contentId}
          disabled={scope === null}
        >
          <Download size={14} strokeWidth={1.75} />
          Export
        </Button>
      )}
    >
      <div className="flex flex-col gap-3">
        <div className="grid grid-cols-2 gap-2">
          <label className="flex flex-col gap-1 text-xs text-text-muted">
            Format
            <select
              aria-label="Export format"
              className={SELECT_CLASS}
              value={format}
              onChange={(event) => changeFormat(event.target.value as ExportFormat)}
            >
              <option value="jsonl">JSONL</option>
              <option value="csv">CSV</option>
            </select>
          </label>
          <label className="flex flex-col gap-1 text-xs text-text-muted">
            Bodies
            <select
              aria-label="Export bodies"
              className={SELECT_CLASS}
              value={bodies}
              onChange={(event) => setBodies(event.target.value as ExportBodies)}
            >
              <option value="none">None</option>
              <option value="text">Flattened text</option>
              {format === 'jsonl' && <option value="full">Full decoded</option>}
            </select>
          </label>
        </div>

        <div className="text-xs text-text-muted" aria-live="polite">
          {countQuery.isPending
            ? 'Counting matching requests…'
            : countQuery.isError
              ? 'Could not count matching requests.'
              : `${count ?? 0} ${count === 1 ? 'request' : 'requests'} will be exported.`}
        </div>

        <a
          href={canDownload ? href : undefined}
          aria-disabled={!canDownload}
          className={cn(
            'inline-flex h-7 items-center justify-center gap-1.5 rounded-control bg-accent px-3 text-sm font-medium text-accent-fg transition-opacity hover:opacity-90',
            !canDownload && 'pointer-events-none opacity-50',
          )}
        >
          <Download size={14} strokeWidth={1.75} />
          Export {format.toUpperCase()}
        </a>
      </div>
    </Popover>
  )
}
