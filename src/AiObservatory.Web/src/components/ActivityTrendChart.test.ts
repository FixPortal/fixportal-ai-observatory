import { describe, expect, it } from 'vitest'
import { toActivityChartRows } from './activityTrendRows'

describe('toActivityChartRows', () => {
  it('clamps parallel session minutes at zero on idle days', () => {
    const result = toActivityChartRows([
      { date: '2026-07-01', activeSeconds: 600, wallClockSeconds: 7200 },
    ])

    expect(result[0].wallClockMinutes).toBe(10)
    expect(result[0].overlapMinutes).toBe(0)
  })

  it('keeps parallel session minutes when active time exceeds wall clock', () => {
    const result = toActivityChartRows([
      { date: '2026-07-01', activeSeconds: 7200, wallClockSeconds: 3600 },
    ])

    expect(result[0].wallClockMinutes).toBe(60)
    expect(result[0].overlapMinutes).toBe(60)
  })
})
