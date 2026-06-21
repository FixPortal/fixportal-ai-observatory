/**
 * Returns the ISO date (yyyy-MM-dd) of the most recent occurrence of
 * billingDay on or before today, handling month-end clamping.
 *
 * E.g. billingDay=31 in February returns the last day of February.
 */
export function currentBillingPeriodStart(billingDay: number, today: string): string {
  const [yearStr, monthStr, dayStr] = today.split('-')
  const year = parseInt(yearStr, 10)
  const month = parseInt(monthStr, 10) // 1–12
  const day = parseInt(dayStr, 10)

  // Days in month m (1-indexed) of year y.
  // new Date(y, m, 0) uses JS's 0-based month index; day 0 = last day of prior month.
  const daysIn = (y: number, m: number) => new Date(y, m, 0).getDate()
  const clamp = (y: number, m: number) => Math.min(billingDay, daysIn(y, m))

  const clampedThisMonth = clamp(year, month)
  if (day >= clampedThisMonth) {
    return `${year}-${monthStr}-${String(clampedThisMonth).padStart(2, '0')}`
  }

  // Billing day hasn't occurred yet — period started last month.
  const prevMonth = month === 1 ? 12 : month - 1
  const prevYear = month === 1 ? year - 1 : year
  const d = clamp(prevYear, prevMonth)
  return `${prevYear}-${String(prevMonth).padStart(2, '0')}-${String(d).padStart(2, '0')}`
}
