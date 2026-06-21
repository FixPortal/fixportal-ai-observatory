/** Minimal shape needed by computeBurnRate. Satisfied structurally by DailyAggregate. */
export interface BurnRateAggregate {
  date: string
  costUsd: number
}

export function computeBurnRate(
  aggregates: BurnRateAggregate[],
  daysInRange: number
): { dailyAvgUsd: number; projectedMonthlyUsd: number } | null {
  const distinctDates = new Set(aggregates.map(a => a.date))
  if (distinctDates.size < 3) return null
  const totalSpend = aggregates.reduce((sum, a) => sum + a.costUsd, 0)
  const dailyAvgUsd = totalSpend / daysInRange
  return { dailyAvgUsd, projectedMonthlyUsd: dailyAvgUsd * 30 }
}
