export interface BilledReportingEntry {
  occurredOn: string
  vendorId: string
  amountGbp: number
}

export interface BilledReportingVendor {
  id: string
  displayName: string
}

export interface BilledReportingSummary {
  totalGbp: number
  dailyAverageGbp: number
  projectedMonthlyGbp: number
  topVendorName: string
  topVendorGbp: number
}

export interface BilledDailySeries {
  date: string
  amountGbp: number
}

export interface BilledVendorSeries {
  vendorId: string
  name: string
  amountGbp: number
}

const vendorNames = (vendors: BilledReportingVendor[]) => new Map(vendors.map(vendor => [vendor.id, vendor.displayName]))

export function summarizeBilledReporting(
  entries: BilledReportingEntry[],
  vendors: BilledReportingVendor[],
  daysInRange: number,
): BilledReportingSummary | null {
  if (entries.length === 0) return null

  const totalsByVendor = new Map<string, number>()
  let totalGbp = 0
  for (const entry of entries) {
    totalGbp += entry.amountGbp
    totalsByVendor.set(entry.vendorId, (totalsByVendor.get(entry.vendorId) ?? 0) + entry.amountGbp)
  }

  const [topVendorId, topVendorGbp] = [...totalsByVendor.entries()]
    .reduce((top, current) => current[1] > top[1] ? current : top, ['', Number.NEGATIVE_INFINITY])
  const dailyAverageGbp = totalGbp / daysInRange
  return {
    totalGbp,
    dailyAverageGbp,
    projectedMonthlyGbp: dailyAverageGbp * 30,
    topVendorName: vendorNames(vendors).get(topVendorId) ?? 'Unknown vendor',
    topVendorGbp,
  }
}

export function buildBilledDailySeries(entries: BilledReportingEntry[]): BilledDailySeries[] {
  const totalsByDate = new Map<string, number>()
  for (const entry of entries) {
    totalsByDate.set(entry.occurredOn, (totalsByDate.get(entry.occurredOn) ?? 0) + entry.amountGbp)
  }
  return [...totalsByDate.entries()]
    .map(([date, amountGbp]) => ({ date, amountGbp }))
    .toSorted((a, b) => a.date.localeCompare(b.date))
}

export function buildBilledVendorSeries(
  entries: BilledReportingEntry[],
  vendors: BilledReportingVendor[],
): BilledVendorSeries[] {
  const totalsByVendor = new Map<string, number>()
  for (const entry of entries) {
    totalsByVendor.set(entry.vendorId, (totalsByVendor.get(entry.vendorId) ?? 0) + entry.amountGbp)
  }
  const names = vendorNames(vendors)
  return [...totalsByVendor.entries()].map(([vendorId, amountGbp]) => ({
    vendorId,
    name: names.get(vendorId) ?? 'Unknown vendor',
    amountGbp,
  }))
}
