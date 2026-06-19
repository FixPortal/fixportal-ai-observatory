import { useMemo } from 'react'
import { Card } from '../design'
import { useAggregates, useInsights, AGGREGATES_DAYS_RANGE } from '../api/queries'
import { useUsdToGbp, formatGbp } from '../lib/currency'
import { formatInt } from '../lib/format'
import { getProvider } from '../config/providers'

// Plain-language explanation of the cache figures, surfaced as a hover/focus
// tooltip (no popover — this dashboard has no shadowed overlay layer).
const CACHE_HELP =
  'Cache hit: the share of prompt (input) tokens served from the provider\'s ' +
  'prompt cache instead of being re-read at full price. ' +
  'Saved: the estimated value of those cached reads versus paying the full ' +
  'input list-price for them. This is notional — on a flat subscription no cash ' +
  'is actually saved; it is what the caching would be worth at API list prices.'

export default function SummaryCards() {
  const aggregates = useAggregates()
  const insights = useInsights()
  const rate = useUsdToGbp()

  // Single pass over aggregates for all four derived values.
  const { totalSpend, totalInputTokens, totalOutputTokens, totalCacheRead, estimatedSavings, topModel } = useMemo(() => {
    let totalSpend = 0
    let totalInputTokens = 0
    let totalOutputTokens = 0
    let totalCacheRead = 0
    let estimatedSavings = 0
    const modelCosts: Record<string, number> = {}
    for (const a of aggregates) {
      totalSpend += a.costUsd
      totalInputTokens += a.inputTokens
      totalOutputTokens += a.outputTokens
      totalCacheRead += (a.cacheReadTokens ?? 0)
      
      // Calculate estimated prompt cache savings in USD based on provider pricing
      const cacheSavings = getProvider(a.provider)?.cacheSavingsPerToken
      if (cacheSavings != null) {
        estimatedSavings += (a.cacheReadTokens ?? 0) * cacheSavings
      }
      
      modelCosts[a.model] = (modelCosts[a.model] ?? 0) + a.costUsd
    }
    const topModel = Object.entries(modelCosts).reduce<[string, number] | undefined>(
      (best, entry) => best == null || entry[1] > best[1] ? entry : best,
      undefined
    )
    return { totalSpend, totalInputTokens, totalOutputTokens, totalCacheRead, estimatedSavings, topModel }
  }, [aggregates])

  const unread = useMemo(() => insights.filter(i => !i.acknowledged).length, [insights])
  const totalTokens = totalInputTokens + totalOutputTokens
  // Share of prompt tokens served from cache = cacheRead / (cacheRead + fresh input).
  // (input_tokens excludes cache reads, so the denominator is the whole prompt.)
  const promptTokens = totalCacheRead + totalInputTokens
  const cacheHitRate = promptTokens > 0 ? (totalCacheRead / promptTokens) * 100 : 0

  return (
    <div className="summary-cards">
      <Card>
        <div className="card-label">Spend · {AGGREGATES_DAYS_RANGE} days</div>
        <div className="card-value card-value--lead">{formatGbp(totalSpend, rate)}</div>
      </Card>
      <Card>
        <div className="card-label">Tokens</div>
        <div className="card-value">{totalTokens === 0 ? '—' : `${(totalTokens / 1_000_000).toFixed(1)}M`}</div>
        {totalTokens > 0 && (
          <div className="card-sub">
            <div>{formatInt(totalInputTokens)} in / {formatInt(totalOutputTokens)} out</div>
            {totalCacheRead > 0 && (
              <div style={{ marginTop: 'var(--space-1)', color: 'var(--ok-text)', fontWeight: 500 }}>
                {(cacheHitRate / 100).toLocaleString(undefined, { style: 'percent', maximumFractionDigits: 0 })} cache hit (saved {formatGbp(estimatedSavings, rate)})
                {' '}
                <span
                  className="info-hint"
                  role="note"
                  aria-label={CACHE_HELP}
                  title={CACHE_HELP}
                >
                  &#9432;
                </span>
              </div>
            )}
          </div>
        )}
      </Card>
      <Card>
        <div className="card-label">Top model</div>
        <div className="card-value card-value--model">{topModel?.[0] ?? '—'}</div>
        <div className="card-sub">{topModel ? formatGbp(topModel[1], rate) : formatGbp(0, rate)}</div>
      </Card>
      <Card>
        <div className="card-label">New insights</div>
        <div className="card-value">{unread}</div>
      </Card>
    </div>
  )
}
