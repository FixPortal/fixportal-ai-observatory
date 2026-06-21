import { describe, it, expect } from 'vitest'
import { computeBurnRate } from './velocity'
import type { DailyAggregate } from '../api/client'

function makeAgg(date: string, costUsd: number): DailyAggregate {
  return { date, provider: 'anthropic', model: 'claude', inputTokens: 0, outputTokens: 0,
    cacheReadTokens: 0, cacheWriteTokens: 0, costUsd, requestCount: 1 }
}

describe('computeBurnRate', () => {
  it('returns null when fewer than 3 days have data', () => {
    expect(computeBurnRate([makeAgg('2026-06-01', 5)], 31)).toBeNull()
    expect(computeBurnRate([makeAgg('2026-06-01', 5), makeAgg('2026-06-02', 3)], 31)).toBeNull()
  })

  it('returns null for empty array', () => {
    expect(computeBurnRate([], 31)).toBeNull()
  })

  it('computes correctly for >= 3 data days', () => {
    const aggs = [
      makeAgg('2026-06-01', 4),
      makeAgg('2026-06-02', 6),
      makeAgg('2026-06-03', 2),
    ]
    const result = computeBurnRate(aggs, 31)
    expect(result).not.toBeNull()
    // totalSpend = 12, dailyAvg = 12/31
    expect(result!.dailyAvgUsd).toBeCloseTo(12 / 31)
    expect(result!.projectedMonthlyUsd).toBeCloseTo((12 / 31) * 30)
  })

  it('handles zero spend', () => {
    const aggs = [
      makeAgg('2026-06-01', 0),
      makeAgg('2026-06-02', 0),
      makeAgg('2026-06-03', 0),
    ]
    const result = computeBurnRate(aggs, 31)
    expect(result).not.toBeNull()
    expect(result!.dailyAvgUsd).toBe(0)
  })
})
