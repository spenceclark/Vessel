import type { ReactNode } from 'react'
import type { Decision, Decisions, DecisionText } from '@/render'
import { Badge } from '@/components/ui/badge'
import { PrettyJson } from '@/components/PrettyJson'
import { ToolContent } from '@/components/ToolCallCard'
import { cn } from '@/lib/utils'

/**
 * #113 — the `typesafe-systemone` view: the state, then one card per question carrying its
 * answer when there is one (request-only rows render the same cards without it). Everything
 * shown is a string or number the extractor produced; captured content never becomes a
 * `src`/`href`.
 */
export function DecisionsView({ decisions }: { decisions: Decisions }) {
  return (
    <div className="flex flex-col gap-3 p-3">
      {decisions.state && (
        <Card label="state">
          <WireText value={decisions.state} />
        </Card>
      )}
      {decisions.items.map((item) => (
        <DecisionCard key={item.key} item={item} />
      ))}
    </div>
  )
}

function DecisionCard({ item }: { item: Decision }) {
  const { answer } = item
  // Choice and score bars already carry each option's rubric; listing criteria again would repeat it.
  const showCriteria = item.criteria.length > 0 && !(answer && answer.bars.length > 1)

  return (
    <Card label={item.key} badge={<Badge variant="info">{item.type}</Badge>}>
      <div className="flex flex-col gap-2">
        {item.instructions && <WireText value={item.instructions} />}

        {showCriteria && (
          <dl className="flex flex-col gap-0.5 text-xs">
            {item.criteria.map((c) => (
              <div key={c.label} className="flex gap-3">
                <dt className="shrink-0 font-mono text-text-secondary">{c.label}</dt>
                {c.text && <dd className="text-text-muted">{c.text}</dd>}
              </div>
            ))}
          </dl>
        )}

        {answer && (
          <div className="flex flex-col gap-1.5 border-t border-border pt-2">
            <div className="flex items-baseline gap-3">
              <span className="font-mono text-sm font-semibold text-text">{answer.value}</span>
              {answer.confidence !== undefined && <span className="text-xs text-text-muted">confidence {answer.confidence}</span>}
            </div>
            {answer.scale && (
              <Meter label="score" valueText={`${answer.scale.value} / ${answer.scale.max}`} fraction={answer.scale.value / answer.scale.max} emphasized />
            )}
            {answer.bars.map((bar) => (
              <Meter
                key={bar.label}
                label={bar.label}
                valueText={`${Math.round(bar.probability * 100)}%`}
                fraction={bar.probability}
                emphasized={bar.chosen !== false}
                bold={bar.chosen}
                note={bar.note}
              />
            ))}
          </div>
        )}

        {item.rawJson !== undefined && <PrettyJson body={{ text: item.rawJson }} />}
      </div>
    </Card>
  )
}

/** A wire string verbatim; an object/array as the #85 field list (arrays fall to pretty JSON there). */
function WireText({ value }: { value: DecisionText }) {
  return value.json ? (
    <ToolContent content={value.text} />
  ) : (
    <pre className="whitespace-pre-wrap break-words font-mono text-base text-text">{value.text}</pre>
  )
}

function Card({ label, badge, children }: { label: string; badge?: ReactNode; children: ReactNode }) {
  return (
    <div className="rounded-control border border-border p-2.5">
      <div className="mb-1.5 flex items-center gap-2">
        <span className="font-mono text-xs font-[550] text-text-muted">{label}</span>
        {badge}
      </div>
      {children}
    </div>
  )
}

/** One horizontal 0-1 bar. Color is a chart token class, never a JS hex (same rule as `ui/chart`). */
function Meter({ label, valueText, fraction, emphasized, bold, note }: {
  label: string
  valueText: string
  fraction: number
  emphasized?: boolean
  bold?: boolean
  note?: string
}) {
  const percent = Math.round(Math.min(1, Math.max(0, fraction)) * 100)
  return (
    <div>
      <div className="flex items-baseline justify-between gap-3 font-mono text-xs">
        <span className={bold ? 'font-semibold text-text' : 'text-text-secondary'}>{label}</span>
        <span className="text-text-muted">{valueText}</span>
      </div>
      <div
        role="meter"
        aria-label={label}
        aria-valuemin={0}
        aria-valuemax={100}
        aria-valuenow={percent}
        className="mt-0.5 h-1.5 overflow-hidden rounded-full bg-surface-3"
      >
        <div className={cn('h-full rounded-full', emphasized ? 'bg-chart-1' : 'bg-chart-6')} style={{ width: `${percent}%` }} />
      </div>
      {note && <p className="mt-0.5 text-xs text-text-muted">{note}</p>}
    </div>
  )
}
