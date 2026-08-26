import { describe, expect, it } from 'vitest'
import { buildBilledDailySeries, buildBilledVendorSeries, summarizeBilledReporting } from './billedReporting'

const entries = [
  { occurredOn: '2026-08-01', vendorId: 'anthropic', amountGbp: 20 },
  { occurredOn: '2026-08-01', vendorId: 'anthropic', amountGbp: -5 },
  { occurredOn: '2026-08-02', vendorId: 'openai', amountGbp: 9 },
]

const vendors = [
  { id: 'anthropic', displayName: 'Anthropic' },
  { id: 'openai', displayName: 'OpenAI' },
]

describe('billed reporting', () => {
  it('summarizes signed GBP ledger entries for the reporting cards', () => {
    expect(summarizeBilledReporting(entries, vendors, 2)).toEqual({
      totalGbp: 24,
      dailyAverageGbp: 12,
      projectedMonthlyGbp: 360,
      topVendorName: 'Anthropic',
      topVendorGbp: 15,
    })
  })

  it('groups signed ledger entries into billed daily and vendor chart series', () => {
    expect(buildBilledDailySeries(entries)).toEqual([
      { date: '2026-08-01', amountGbp: 15 },
      { date: '2026-08-02', amountGbp: 9 },
    ])
    expect(buildBilledVendorSeries(entries, vendors)).toEqual([
      { vendorId: 'anthropic', name: 'Anthropic', amountGbp: 15 },
      { vendorId: 'openai', name: 'OpenAI', amountGbp: 9 },
    ])
  })

  it('keeps a ledger with no rows distinct from a zero net ledger', () => {
    expect(summarizeBilledReporting([], vendors, 2)).toBeNull()
    expect(summarizeBilledReporting([
      { occurredOn: '2026-08-01', vendorId: 'anthropic', amountGbp: 5 },
      { occurredOn: '2026-08-01', vendorId: 'anthropic', amountGbp: -5 },
    ], vendors, 2)).toEqual({
      totalGbp: 0,
      dailyAverageGbp: 0,
      projectedMonthlyGbp: 0,
      topVendorName: 'Anthropic',
      topVendorGbp: 0,
    })
  })

  it('retains entries for vendors absent from the catalog', () => {
    expect(buildBilledVendorSeries([
      { occurredOn: '2026-08-01', vendorId: 'missing', amountGbp: 7 },
    ], vendors)).toEqual([
      { vendorId: 'missing', name: 'Unknown vendor', amountGbp: 7 },
    ])
  })
})
