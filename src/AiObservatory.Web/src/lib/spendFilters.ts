// lib is a pure-helper layer and must not depend on api (see architecture.spec.ts), so
// these take a structural shape rather than importing SpendEntry from '../api/client'.
// The generic on filterEntries preserves the caller's concrete type (SpendEntry) through
// inference, so Task 7/8 callers still get full SpendEntry[] back, not this narrowed shape.
interface SpendRowShape {
  vendorId: string
  categoryId: string
}

export interface SpendFilter {
  vendorId?: string
  categoryId?: string
  /** Categories switched off in the legend. The headline total follows this. */
  excludedCategoryIds?: string[]
}

/** Pure — one filter state drives the totals and the table alike. */
export function filterEntries<T extends SpendRowShape>(entries: T[], filter: SpendFilter): T[] {
  const excluded = new Set(filter.excludedCategoryIds ?? [])
  return entries.filter(e =>
    (filter.vendorId == null || e.vendorId === filter.vendorId) &&
    (filter.categoryId == null || e.categoryId === filter.categoryId) &&
    !excluded.has(e.categoryId))
}

/**
 * Sums the GBP column, never `amount`. `amountGbp` was converted at the charge date and
 * frozen; re-converting here would make historical totals drift with the exchange rate.
 */
export function totalGbp(entries: { amountGbp: number }[]): number {
  return entries.reduce((sum, e) => sum + e.amountGbp, 0)
}
