import { useMemo } from 'react'
import { Card } from '../design/Card'
import { useAggregates, useInsights, useSpendEntries, AGGREGATES_DAYS_RANGE, dashboardDateRange } from '../api/queries'
import { useUsdToGbp, formatGbp, gbp } from '../lib/currency'
import { formatInt } from '../lib/format'
import { summarizeCosts } from '../lib/costSummary'
import { InfoPopover } from './InfoPopover'

const notReported = 'Not reported'

export default function SummaryCards() {
  const range = useMemo(() => dashboardDateRange(), [])
  const aggregates = useAggregates(range.from, range.to)
  const { entries: spendEntries } = useSpendEntries(range.from, range.to)
  const insights = useInsights()
  const rate = useUsdToGbp()
  const summary = summarizeCosts(aggregates, spendEntries)

  const { totalInputTokens, totalOutputTokens, totalCacheRead } = useMemo(() => aggregates.reduce((total, aggregate) => ({
    totalInputTokens: total.totalInputTokens + aggregate.inputTokens,
    totalOutputTokens: total.totalOutputTokens + aggregate.outputTokens,
    totalCacheRead: total.totalCacheRead + aggregate.cacheReadTokens,
  }), { totalInputTokens: 0, totalOutputTokens: 0, totalCacheRead: 0 }), [aggregates])

  const unread = insights.filter(insight => !insight.acknowledged).length
  const totalTokens = totalInputTokens + totalOutputTokens
  const promptTokens = totalCacheRead + totalInputTokens
  const cacheHitRate = promptTokens > 0 ? totalCacheRead / promptTokens : 0

  return (
    <div className="summary-cards">
      <Card>
        <div className="card-label card-label--row">
          Billed spend · {AGGREGATES_DAYS_RANGE} days
          <InfoPopover id="billed-spend-info" title="Billed spend" className="info-popover--summary">
            <p>Financial ledger entries reported by a provider or recorded as spend. It does not include token-rate estimates or subscription notional value.</p>
            <p>This uses the same rolling {AGGREGATES_DAYS_RANGE}-day window as every financial lane.</p>
          </InfoPopover>
        </div>
        <div className="card-value card-value--lead">{summary.billedGbp === null ? notReported : gbp(summary.billedGbp)}</div>
      </Card>
      <Card>
        <div className="card-label card-label--row">
          List-price estimate
          <InfoPopover id="list-price-info" title="List-price estimate" className="info-popover--summary">
            <p>API usage rated from public list prices. USD is converted for display; this is not billed spend.</p>
            <p>This uses the same rolling {AGGREGATES_DAYS_RANGE}-day window as every financial lane.</p>
          </InfoPopover>
        </div>
        <div className="card-value">{summary.listPriceEstimateUsd === null ? notReported : formatGbp(summary.listPriceEstimateUsd, rate)}</div>
        <div className="card-sub">USD basis; shown in GBP when reported</div>
      </Card>
      <Card>
        <div className="card-label card-label--row">
          Provider estimate
          <InfoPopover id="provider-estimate-info" title="Provider estimate" className="info-popover--summary">
            <p>A provider-produced estimate, not an invoice. USD is converted for display.</p>
            <p>This uses the same rolling {AGGREGATES_DAYS_RANGE}-day window as every financial lane.</p>
          </InfoPopover>
        </div>
        <div className="card-value">{summary.providerEstimateUsd === null ? notReported : formatGbp(summary.providerEstimateUsd, rate)}</div>
        <div className="card-sub">USD basis; shown in GBP when reported</div>
      </Card>
      <Card>
        <div className="card-label card-label--row">
          Subscription notional
          <InfoPopover id="subscription-notional-info" title="Subscription notional" className="info-popover--summary">
            <p>API-list-price comparison for subscription or local activity. No corresponding money changed hands. USD is converted for display.</p>
            <p>This uses the same rolling {AGGREGATES_DAYS_RANGE}-day window as every financial lane.</p>
          </InfoPopover>
        </div>
        <div className="card-value">{summary.notionalUsd === null ? notReported : formatGbp(summary.notionalUsd, rate)}</div>
        <div className="card-sub">USD basis; shown in GBP when reported</div>
      </Card>
      <Card>
        <div className="card-label">Tokens</div>
        <div className="card-value">{aggregates.length === 0 ? notReported : totalTokens === 0 ? '0' : `${(totalTokens / 1_000_000).toFixed(1)}M`}</div>
        {totalTokens > 0 && (
          <div className="card-sub">
            <div>{formatInt(totalInputTokens)} in / {formatInt(totalOutputTokens)} out</div>
            {totalCacheRead > 0 && (
              <div className="card-cache">
                <div>{cacheHitRate.toLocaleString(undefined, { style: 'percent', maximumFractionDigits: 0 })} cache hit</div>
                <div>Cache savings: {summary.cacheSavingsUsd === null ? notReported : `${formatGbp(summary.cacheSavingsUsd, rate)} (server-reported, USD-derived)`}</div>
                {summary.unknownCacheSavingsObservations > 0 && <div>{summary.unknownCacheSavingsObservations} savings observation{summary.unknownCacheSavingsObservations === 1 ? '' : 's'} not reported</div>}
                <InfoPopover id="cache-info" title="Prompt cache" className="info-popover--summary">
                  <p>Cache hit is the share of observed prompt tokens served from cache. Savings are shown only when the server reported them.</p>
                </InfoPopover>
              </div>
            )}
          </div>
        )}
      </Card>
      <Card>
        <div className="card-label">New insights</div>
        <div className="card-value">{unread}</div>
      </Card>
    </div>
  )
}
