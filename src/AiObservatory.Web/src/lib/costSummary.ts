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

export interface CostSummary {
  billedGbp: number | null
  listPriceEstimateUsd: number | null
  providerEstimateUsd: number | null
  notionalUsd: number | null
  unknownCostObservations: number
  cacheSavingsUsd: number | null
  unknownCacheSavingsObservations: number
}

export function summarizeCosts(aggregates: CostAggregate[], spendEntries: SpendAmount[]): CostSummary {
  let billedGbp = spendEntries.length === 0 ? null : 0
  let listPriceEstimateUsd: number | null = null
  let providerEstimateUsd: number | null = null
  let notionalUsd: number | null = null
  let unknownCostObservations = 0
  let cacheSavingsUsd: number | null = null
  let unknownCacheSavingsObservations = 0

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
  }
}
