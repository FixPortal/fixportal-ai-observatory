import { useAdversarialReviewRuns, useAdversarialReviewStats } from '../api/queries'
import { providerColor } from '../theme/providerColors'

function formatCost(n: number | null | undefined): string {
  if (n == null) return '—'
  return `$${n.toFixed(4)}`
}

function formatNumber(n: number | null | undefined): string {
  if (n == null) return '—'
  return n.toFixed(2)
}

function formatDate(iso: string): string {
  return iso.slice(0, 10)
}

function capitalize(s: string): string {
  return s.charAt(0).toUpperCase() + s.slice(1)
}

export default function AdversarialReviewPanel() {
  const stats = useAdversarialReviewStats()
  const runs = useAdversarialReviewRuns()

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
                <th>Reviewer</th>
                <th>Model</th>
                <th>Runs</th>
                <th>Avg cost/run</th>
                <th>Avg raised</th>
                <th>Avg accepted</th>
                <th>Avg cost/finding</th>
              </tr>
            </thead>
            <tbody>
              {stats.map(s => (
                <tr key={`${s.reviewer}|${s.model}`}>
                  <td>
                    <span
                      className="model-table__dot"
                      style={{ background: providerColor(s.reviewer) }}
                      title={s.reviewer}
                    />
                    {capitalize(s.reviewer)}
                  </td>
                  <td>{s.model}</td>
                  <td>{s.runCount}</td>
                  <td>{formatCost(s.avgCostPerRun)}</td>
                  <td>{formatNumber(s.avgIssuesRaised)}</td>
                  <td>{formatNumber(s.avgIssuesAccepted)}</td>
                  <td>{formatCost(s.avgCostPerAcceptedFinding)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>

      <div className="panel">
        <div className="panel-title">Recent runs</div>
        {runs.length === 0 ? (
          <p className="panel-empty">No runs recorded yet.</p>
        ) : (
          <table className="model-table">
            <thead>
              <tr>
                <th>Date</th>
                <th>Reviewer</th>
                <th>Model</th>
                <th>Raised</th>
                <th>Accepted</th>
                <th>Cost/run</th>
                <th>Cost/finding</th>
                <th>Duration</th>
              </tr>
            </thead>
            <tbody>
              {runs.map(r => (
                <tr key={r.id}>
                  <td>{formatDate(r.recordedAt)}</td>
                  <td>
                    <span
                      className="model-table__dot"
                      style={{ background: providerColor(r.reviewer) }}
                      title={r.reviewer}
                    />
                    {capitalize(r.reviewer)}
                  </td>
                  <td>{r.model}</td>
                  <td>{r.issuesRaised}</td>
                  <td>{r.issuesAccepted}</td>
                  <td>{formatCost(r.costUsd)}</td>
                  <td>{formatCost(r.costPerAcceptedFinding)}</td>
                  <td>{(r.reviewDurationMs / 1000).toFixed(1)}s</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>
    </div>
  )
}
