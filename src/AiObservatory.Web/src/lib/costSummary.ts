export interface CostAggregate {
  costBasis: string
  costUsd: number
  unknownCostCount: number
  cacheReadTokens: number
  cacheWriteTokens: number
  cacheSavingsUsd: number
  unknownCacheSavingsCount: number
  requestCount: number
}

export interface SpendAmount {
  amountGbp: number
}

interface TokenAggregate {
  inputTokens: number
  outputTokens: number
  cacheReadTokens: number
  cacheWriteTokens: number
}

export const observedInputTokens = (aggregate: TokenAggregate) =>
  aggregate.inputTokens + aggregate.cacheReadTokens + aggregate.cacheWriteTokens

export const observedTokens = (aggregate: TokenAggregate) => observedInputTokens(aggregate) + aggregate.outputTokens

// "none" is a legitimate, DB-constraint-enforced always-zero basis (e.g. Copilot daily
// reports); a zero-cost row under any basis carries nothing worth flagging either way.
const isUnclassifiedSpend = (aggregate: CostAggregate) => aggregate.costBasis !== 'none' && aggregate.costUsd !== 0

export interface CostSummary {
  billedGbp: number | null
  listPriceEstimateUsd: number | null
  providerEstimateUsd: number | null
  notionalUsd: number | null
  unknownCostObservations: number
  cacheSavingsUsd: number | null
  unknownCacheSavingsObservations: number
  /** costUsd from rows whose costBasis is none of the three named buckets above (e.g.
   * "unknown", or an aggregate-sourced "billed" row — a smaller, distinct signal from
   * the ledger-sourced Billed spend card) — a known nonzero dollar figure that none of
   * the summary cards would otherwise show. Excludes "none", the legitimate always-zero
   * basis Copilot daily reports use (a real CK_CopilotDailyReport_NoCost DB constraint),
   * and any zero-cost row in general, so the note only surfaces real dropped spend
   * rather than firing permanently at £0.00. Null when nothing fell into this bucket. */
  unclassifiedUsd: number | null
}

export function summarizeCosts(aggregates: CostAggregate[], spendEntries: SpendAmount[]): CostSummary {
  let billedGbp = spendEntries.length === 0 ? null : 0
  let listPriceEstimateUsd: number | null = null
  let providerEstimateUsd: number | null = null
  let notionalUsd: number | null = null
  let unknownCostObservations = 0
  let cacheSavingsUsd: number | null = null
  let unknownCacheSavingsObservations = 0
  let unclassifiedUsd: number | null = null

  for (const entry of spendEntries) billedGbp! += entry.amountGbp

  for (const aggregate of aggregates) {
    unknownCostObservations += aggregate.unknownCostCount
    if (aggregate.cacheReadTokens + aggregate.cacheWriteTokens > 0) {
      unknownCacheSavingsObservations += aggregate.unknownCacheSavingsCount
      if (aggregate.requestCount > aggregate.unknownCacheSavingsCount) {
        cacheSavingsUsd = (cacheSavingsUsd ?? 0) + aggregate.cacheSavingsUsd
      }
    }

    if (aggregate.requestCount <= aggregate.unknownCostCount) continue
    switch (aggregate.costBasis) {
      case 'listPriceEstimate':
        listPriceEstimateUsd = (listPriceEstimateUsd ?? 0) + aggregate.costUsd
        break
      case 'providerEstimated':
        providerEstimateUsd = (providerEstimateUsd ?? 0) + aggregate.costUsd
        break
      case 'notional':
        notionalUsd = (notionalUsd ?? 0) + aggregate.costUsd
        break
      default:
        // A row with a known, nonzero costUsd but a cost basis outside the three named
        // buckets (e.g. "unknown", "billed") would otherwise vanish from every summary
        // card with no indication anything was left out.
        if (isUnclassifiedSpend(aggregate)) {
          unclassifiedUsd = (unclassifiedUsd ?? 0) + aggregate.costUsd
        }
        break
    }
  }

  return {
    billedGbp,
    listPriceEstimateUsd,
    providerEstimateUsd,
    notionalUsd,
    unknownCostObservations,
    cacheSavingsUsd,
    unknownCacheSavingsObservations,
    unclassifiedUsd,
  }
}
