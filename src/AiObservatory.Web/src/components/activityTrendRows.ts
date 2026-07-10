import type { DailyActivity } from '../api/client'

export interface ActivityChartRow {
  date: string
  wallClockMinutes: number
  overlapMinutes: number
}

export function toActivityChartRows(daily: DailyActivity[]): ActivityChartRow[] {
  return daily
    .toSorted((a, b) => a.date.localeCompare(b.date))
    .map((d) => {
      const activeMinutes = Math.round(d.activeSeconds / 60)
      const wallClockMinutes = Math.round(d.wallClockSeconds / 60)
      return {
        date: d.date,
        wallClockMinutes: Math.min(activeMinutes, wallClockMinutes),
        overlapMinutes: Math.max(0, activeMinutes - wallClockMinutes),
      }
    })
}
