import { LineChart, type LineSeries } from '@/components/ui/chart/LineChart'

function seriesLabel(key: string | null): string {
  return key ?? '(none)'
}

/**
 * #25 live-use feedback (round 2), tier 2 — "small multiples: the canonical answer to
 * 'meaningful alone, meaningless together'". One mini line chart per series, in the same
 * 2-column grid the aggregate cards already use, sharing one y-axis domain across all of
 * them so relative context sizes stay comparable card-to-card (a lone-series chart would
 * otherwise rescale to its own peak and look identical to a much smaller agent's).
 */
export function ContextGrowthSmallMultiples({
  series,
  colors,
  formatValue,
  formatTime,
  onSelectPoint,
}: {
  series: LineSeries[]
  colors: string[]
  formatValue: (v: number) => string
  formatTime: (iso: string) => string
  onSelectPoint?: (id: number) => void
}) {
  const sharedMax = Math.max(0, ...series.flatMap((s) => s.points.map((p) => p.v)))

  return (
    <div className="grid grid-cols-1 gap-3 min-[1100px]:grid-cols-2">
      {series.map((s, index) => {
        const peak = Math.max(0, ...s.points.map((p) => p.v))
        return (
          <section key={s.key ?? '(none)'} className="rounded-control bg-surface-3 p-3">
            <h4 className="text-xs font-[550] uppercase tracking-[0.06em] text-text-muted">{seriesLabel(s.key)}</h4>
            <div className="mt-2">
              <LineChart
                series={[s]}
                colors={[colors[index]!]}
                height={180}
                label={`${seriesLabel(s.key)}: tokens in per request over time, peak ${peak.toLocaleString('en-US')}`}
                formatValue={formatValue}
                formatTime={formatTime}
                onSelectPoint={onSelectPoint}
                yDomainMax={sharedMax}
                showLegend={false}
              />
            </div>
          </section>
        )
      })}
    </div>
  )
}
