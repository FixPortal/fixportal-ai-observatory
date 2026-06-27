import { useMemo } from 'react'
import { useAdversarialReviewRuns, useAdversarialReviewStats } from '../api/queries'
import { participantColor } from '../theme/providerColors'
import { CollapsiblePanel } from './CollapsiblePanel'
import { groupRuns, formatSeconds, formatMinutes, bankersRound, type RunGroup } from './adversarialReviewGrouping'

const PUTATIVE_NOTE = '~ putative cost — estimated from a combined token count (subscription model, no exact per-call billing)'

// Anthropic reviewer/judge costs are blended estimates from the Agent tool's
// combined token count; OpenAI and Google costs are exact from their APIs.
function isPutativeCost(reviewer: string): boolean {
  return reviewer === 'anthropic'
}

function formatCost(n: number | null | undefined, estimated = false): string {
  if (n == null) return '—'
  return `${estimated ? '~' : ''}$${bankersRound(n, 2).toFixed(2)}`
}
function formatCount(n: number | null | undefined): string {
  if (n == null) return '—'
  return `${bankersRound(n, 0)}`
}
function capitalize(s: string): string {
  return s.charAt(0).toUpperCase() + s.slice(1)
}

function RunSummary({ group }: { group: RunGroup }) {
  const t = group.totals
  const hasPutativeCost = group.participants.some(p => isPutativeCost(p.reviewer))
  return (
    <span className="adv-run__summary">
      {group.summary && <span className="adv-run__meta">{group.recordedAt.slice(0, 16).replace('T', ' ')}</span>}
      {group.repo && <span className="adv-run__meta">{group.repo}</span>}
      <span className={`adv-run__badge ${group.isComplete ? 'adv-run__badge--ok' : 'adv-run__badge--warn'}`}>
        {group.isComplete ? 'complete' : 'incomplete'}
      </span>
      {!group.isComplete && <span className="adv-run__meta">{group.statusReason}</span>}
      <span className="adv-run__totals">
        raised <b>{t.raised}</b> · accepted <b>{t.accepted}</b> · <b title={hasPutativeCost ? PUTATIVE_NOTE : undefined}>{formatCost(t.costUsd, hasPutativeCost)}</b> · <b>{formatSeconds(t.durationMs)}</b>
      </span>
    </span>
  )
}

export default function AdversarialReviewPanel() {
  const stats = useAdversarialReviewStats()
  const runs = useAdversarialReviewRuns()
  const groups = useMemo(() => groupRuns(runs), [runs])

  return (
    <div className="adv-review-panel">
      <div className="panel">
        <div className="panel-title">Stats by reviewer &amp; model</div>
        {stats.length === 0 ? (
          <p className="panel-empty">No adversarial-review runs recorded yet.</p>
        ) : (
          <table className="model-table">
            <thead>
              <tr>
                <th>Reviewer</th><th>Model</th><th>Runs</th><th>Avg cost/run</th>
                <th>Avg raised</th><th>Avg accepted</th><th>Avg cost/finding</th><th>Avg dur</th>
              </tr>
            </thead>
            <tbody>
              {stats.map(s => (
                <tr key={`${s.reviewer}|${s.model}|${s.role}`}>
                  <td>
                    <span className="model-table__dot" style={{ background: participantColor(s.reviewer, s.role) }} title={`${s.reviewer} ${s.role}`} />
                    {capitalize(s.reviewer)}{s.role === 'judge' && ' (judge)'}
                  </td>
                  <td>{s.model}</td>
                  <td>{s.runCount}</td>
                  <td title={isPutativeCost(s.reviewer) ? PUTATIVE_NOTE : undefined}>{formatCost(s.avgCostPerRun, isPutativeCost(s.reviewer))}</td>
                  <td>{formatCount(s.avgIssuesRaised)}</td>
                  <td>{formatCount(s.avgIssuesAccepted)}</td>
                  <td title={isPutativeCost(s.reviewer) ? PUTATIVE_NOTE : undefined}>{formatCost(s.avgCostPerAcceptedFinding, isPutativeCost(s.reviewer))}</td>
                  <td>{formatMinutes(s.avgDurationMs)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>

      <div className="panel">
        <div className="panel-title">Recent runs</div>
        {groups.length === 0 ? (
          <p className="panel-empty">No runs recorded yet.</p>
        ) : (
          groups.map(group => (
            <CollapsiblePanel
              key={group.runId}
              id={`adv-run-${group.runId}`}
              title={group.summary ?? group.recordedAt.slice(0, 16).replace('T', ' ')}
              summary={<RunSummary group={group} />}
            >
              <table className="model-table">
                <thead>
                  <tr>
                    <th>Reviewer</th><th>Model</th><th>Raised</th><th>Accepted</th>
                    <th>Cost</th><th>Cost/finding</th><th>Duration</th>
                  </tr>
                </thead>
                <tbody>
                  {group.participants.map(p => (
                    <tr key={p.id}>
                      <td>
                        <span className="model-table__dot" style={{ background: participantColor(p.reviewer, p.role) }} title={`${p.reviewer} ${p.role}`} />
                        {capitalize(p.reviewer)}{p.role === 'judge' && ' (judge)'}
                      </td>
                      <td>{p.model}</td>
                      <td>{p.role === 'judge' ? '—' : p.issuesRaised}</td>
                      <td>{p.role === 'judge' ? '—' : p.issuesAccepted}</td>
                      <td title={isPutativeCost(p.reviewer) ? PUTATIVE_NOTE : undefined}>{formatCost(p.costUsd, isPutativeCost(p.reviewer))}</td>
                      <td title={isPutativeCost(p.reviewer) ? PUTATIVE_NOTE : undefined}>{p.role === 'judge' ? '—' : formatCost(p.costPerAcceptedFinding, isPutativeCost(p.reviewer))}</td>
                      <td>{formatSeconds(p.reviewDurationMs)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </CollapsiblePanel>
          ))
        )}
      </div>
      {(stats.some(s => isPutativeCost(s.reviewer)) || groups.some(g => g.participants.some(p => isPutativeCost(p.reviewer)))) && (
        <p className="panel-note">{PUTATIVE_NOTE}</p>
      )}
    </div>
  )
}
