import { useMemo } from 'react'
import { Card } from '../design/Card'
import { computeBurnRate } from '../lib/velocity'
import { useUsdToGbp, formatGbp } from '../lib/currency'
import type { DailyAggregate } from '../api/client'

interface Props {
  aggregates: DailyAggregate[]
  daysInRange: number
}

export default function ReportingCards({ aggregates, daysInRange }: Props) {
  const rate = useUsdToGbp()

  const { totalSpend, topProvider } = useMemo(() => {
    let totalSpend = 0
    const providerSpend: Record<string, number> = {}
    for (const a of aggregates) {
      totalSpend += a.costUsd
      providerSpend[a.provider] = (providerSpend[a.provider] ?? 0) + a.costUsd
    }
    const topProvider = Object.entries(providerSpend).reduce<[string, number] | undefined>(
      (best, e) => best == null || e[1] > best[1] ? e : best,
      undefined
    )
    return { totalSpend, topProvider }
  }, [aggregates])

  const burnRate = computeBurnRate(aggregates, daysInRange)

  return (
    <div className="summary-cards">
      <Card>
        <div className="card-label">Period spend</div>
        <div className="card-value card-value--lead">{formatGbp(totalSpend, rate)}</div>
      </Card>
      <Card>
        <div className="card-label">Daily avg</div>
        <div className="card-value">{burnRate ? formatGbp(burnRate.dailyAvgUsd, rate) : '—'}</div>
      </Card>
      <Card>
        <div className="card-label">Projected / month</div>
        <div className="card-value">{burnRate ? formatGbp(burnRate.projectedMonthlyUsd, rate) : '—'}</div>
        {burnRate && <div className="card-sub">{formatGbp(burnRate.dailyAvgUsd, rate)}/day avg</div>}
      </Card>
      <Card>
        <div className="card-label">Top provider</div>
        <div className="card-value card-value--model">{topProvider?.[0] ?? '—'}</div>
        {topProvider && <div className="card-sub">{formatGbp(topProvider[1], rate)}</div>}
      </Card>
    </div>
  )
}
