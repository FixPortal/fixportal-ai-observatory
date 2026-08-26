import { Card } from '../design/Card'
import { gbp } from '../lib/currency'
import { summarizeBilledReporting, type BilledReportingEntry, type BilledReportingVendor } from '../lib/billedReporting'

interface Props {
  entries: BilledReportingEntry[]
  vendors: BilledReportingVendor[]
  daysInRange: number
}

export default function ReportingCards({ entries, vendors, daysInRange }: Props) {
  const summary = summarizeBilledReporting(entries, vendors, daysInRange)

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
        {summary && <div className="card-sub">{gbp(summary.topVendorGbp)}</div>}
      </Card>
    </div>
  )
}
