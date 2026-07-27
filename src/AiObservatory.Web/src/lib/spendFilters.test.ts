import { describe, it, expect } from 'vitest'
import { filterEntries, totalGbp } from './spendFilters'
import type { SpendEntry } from '../api/client'

function entry(over: Partial<SpendEntry> = {}): SpendEntry {
  return {
    id: crypto.randomUUID(),
    occurredOn: '2026-07-12',
    vendorId: 'v1',
    categoryId: 'c1',
    amount: 80,
    currency: 'GBP',
    amountGbp: 80,
    fxRate: 1,
    description: 'Top-up',
    source: 'csv',
    entryKey: 'k1',
    recordedAt: '2026-07-12T00:00:00Z',
    ...over,
  }
}

describe('filterEntries', () => {
  it('returns everything when no filter is set', () => {
    const rows = [entry(), entry({ categoryId: 'c2' })]
    expect(filterEntries(rows, {})).toHaveLength(2)
  })

  it('filters by category', () => {
    const rows = [entry({ categoryId: 'c1' }), entry({ categoryId: 'c2' })]
    expect(filterEntries(rows, { categoryId: 'c2' })).toHaveLength(1)
  })

  it('filters by vendor', () => {
    const rows = [entry({ vendorId: 'v1' }), entry({ vendorId: 'v2' })]
    expect(filterEntries(rows, { vendorId: 'v1' })).toHaveLength(1)
  })

  it('excludes categories switched off, so the total follows the legend', () => {
    const rows = [entry({ categoryId: 'c1' }), entry({ categoryId: 'c2' })]
    expect(filterEntries(rows, { excludedCategoryIds: ['c1'] })).toHaveLength(1)
  })

  it('combines filters', () => {
    const rows = [
      entry({ vendorId: 'v1', categoryId: 'c1' }),
      entry({ vendorId: 'v1', categoryId: 'c2' }),
      entry({ vendorId: 'v2', categoryId: 'c1' }),
    ]
    expect(filterEntries(rows, { vendorId: 'v1', categoryId: 'c1' })).toHaveLength(1)
  })
})

describe('totalGbp', () => {
  it('sums the GBP column, not the native amount', () => {
    const rows = [entry({ amount: 100, amountGbp: 74 }), entry({ amount: 10, amountGbp: 10 })]
    expect(totalGbp(rows)).toBe(84)
  })

  it('is zero for no rows', () => {
    expect(totalGbp([])).toBe(0)
  })

  it('nets a refund off the charges with no refund-aware special case', () => {
    // The signed amount is the whole design: this plain reduce is every aggregate in the
    // app, and it is correct for refunds without knowing refunds exist.
    const rows = [entry({ amountGbp: 100 }), entry({ amountGbp: -30 })]
    expect(totalGbp(rows)).toBe(70)
  })

  it('can go negative when a period holds more refund than charge', () => {
    const rows = [entry({ amountGbp: 20 }), entry({ amountGbp: -50 })]
    expect(totalGbp(rows)).toBe(-30)
  })

  it('reflects the filter, so the headline is the total of what is on screen', () => {
    const rows = [entry({ categoryId: 'c1', amountGbp: 50 }), entry({ categoryId: 'c2', amountGbp: 25 })]
    expect(totalGbp(filterEntries(rows, { categoryId: 'c1' }))).toBe(50)
  })
})
