import { describe, expect, test } from 'vitest'
import type { DailyAggregate, SpendEntry } from '../api/client'
import { summarizeCosts } from './costSummary'

const aggregate = (overrides: Partial<DailyAggregate>): DailyAggregate => ({
  date: '2026-08-24', provider: 'openai', model: 'gpt-5', sourceId: 'openai-usage-api',
  sourceKind: 'providerApi', usageScope: 'api', costBasis: 'none', inputTokens: 0,
  outputTokens: 0, cacheReadTokens: 0, cacheWriteTokens: 0, cacheWrite1hTokens: 0,
  costUsd: 0, unknownCostCount: 0, cacheSavingsUsd: 0, unknownCacheSavingsCount: 0,
  requestCount: 0, ...overrides,
})

const spend = (amountGbp: number): SpendEntry => ({
  id: crypto.randomUUID(), occurredOn: '2026-08-24', vendorId: 'vendor', categoryId: 'category',
  amount: amountGbp, currency: 'GBP', amountGbp, fxRate: 1, description: null, source: 'manual',
  entryKey: null, recordedAt: '2026-08-24T12:00:00Z',
})

describe('summarizeCosts', () => {
  test('keeps billed, estimated, notional, and unknown observations separate', () => {
    const rows = [
      aggregate({ costBasis: 'billed', costUsd: 99, requestCount: 1 }),
      aggregate({ costBasis: 'listPriceEstimate', costUsd: 2, requestCount: 1 }),
      aggregate({ costBasis: 'providerEstimated', costUsd: 3, requestCount: 1 }),
      aggregate({ costBasis: 'notional', costUsd: 4, requestCount: 1 }),
      aggregate({ costBasis: 'unknown', costUsd: 123, cacheReadTokens: 1, unknownCostCount: 1, unknownCacheSavingsCount: 1, requestCount: 1 }),
    ]

    expect(summarizeCosts(rows, [spend(10), spend(-2)])).toEqual({
      billedGbp: 8,
      listPriceEstimateUsd: 2,
      providerEstimateUsd: 3,
      notionalUsd: 4,
      unknownCostObservations: 1,
      cacheSavingsUsd: null,
      unknownCacheSavingsObservations: 1,
      // The "billed" row above (costUsd: 99) has an unrecognized basis for this
      // summarizer (billed spend comes from the ledger, not aggregates) so it lands
      // in unclassifiedUsd rather than silently vanishing.
      unclassifiedUsd: 99,
    })
  })

  test('surfaces a known cost under an unrecognized basis instead of silently dropping it', () => {
    // Distinct from the fully-unknown row above: here unknownCostCount is 0 (every
    // request has a known cost), but costBasis itself is "unknown" — a real shape
    // seen in seeded data (a provider row whose pricing tier isn't classified).
    // Without a default branch this $31.50 would vanish from every summary card.
    const result = summarizeCosts([
      aggregate({ costBasis: 'unknown', costUsd: 31.5, unknownCostCount: 0, requestCount: 28 }),
    ], [])
    expect(result.unclassifiedUsd).toBe(31.5)
    expect(result.listPriceEstimateUsd).toBeNull()
    expect(result.providerEstimateUsd).toBeNull()
    expect(result.notionalUsd).toBeNull()
  })

  test('also catches "billed" and "none" cost bases reaching the aggregate summarizer', () => {
    const result = summarizeCosts([
      aggregate({ costBasis: 'billed', costUsd: 10, requestCount: 1 }),
      aggregate({ costBasis: 'none', costUsd: 5, requestCount: 1 }),
    ], [])
    expect(result.unclassifiedUsd).toBe(15)
  })

  test('preserves absence separately from a represented legitimate zero', () => {
    expect(summarizeCosts([], []).billedGbp).toBeNull()
    expect(summarizeCosts([aggregate({ costBasis: 'notional', costUsd: 0, requestCount: 1 })], []).notionalUsd).toBe(0)
  })

  test('sums only known server cache savings and reports unknown observations', () => {
    expect(summarizeCosts([
      aggregate({ costBasis: 'listPriceEstimate', cacheReadTokens: 1, cacheSavingsUsd: 1.25, requestCount: 1 }),
      aggregate({ costBasis: 'listPriceEstimate', cacheReadTokens: 1, unknownCacheSavingsCount: 2, requestCount: 2 }),
    ], [])).toMatchObject({ cacheSavingsUsd: 1.25, unknownCacheSavingsObservations: 2 })
  })
})
