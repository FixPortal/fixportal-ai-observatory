import { Card } from '../design/Card'
import { gbp } from '../lib/currency'
import type { BilledReporting } from '../api/client'

interface Props {
  report: BilledReporting | undefined
}

export default function ReportingCards({ report }: Props) {
  const summary = report?.entryCount ? report : undefined
  return (
    <div className="summary-cards">
      <Card>
        <div className="card-label">Billed spend</div>
        <div className="card-value card-value--lead">{summary ? gbp(summary.totalGbp) : '—'}</div>
      </Card>
      <Card>
        <div className="card-label">Daily average</div>
        <div className="card-value">{summary ? gbp(summary.dailyAverageGbp) : '—'}</div>
      </Card>
      <Card>
        <div className="card-label">Projected / month</div>
        <div className="card-value">{summary ? gbp(summary.projectedMonthlyGbp) : '—'}</div>
        {summary && <div className="card-sub">{gbp(summary.dailyAverageGbp)}/day avg</div>}
      </Card>
      <Card>
        <div className="card-label">Top vendor</div>
        <div className="card-value card-value--model">{summary?.topVendorName ?? '—'}</div>
        {summary && <div className="card-sub">{summary.topVendorGbp === null ? '—' : gbp(summary.topVendorGbp)}</div>}
      </Card>
    </div>
  )
}
