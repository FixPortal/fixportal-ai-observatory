import { useMemo } from 'react'
import { Card } from '../design'
import { useAggregates, usePriorPeriodAggregates, useInsights, AGGREGATES_DAYS_RANGE } from '../api/queries'
import { useUsdToGbp, formatGbp, gbp } from '../lib/currency'
import { formatInt } from '../lib/format'
import { getProvider } from '../config/providers'
import { InfoPopover } from './InfoPopover'

export default function SummaryCards() {
  const aggregates = useAggregates()
  const priorAggregates = usePriorPeriodAggregates()
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
      const provider = getProvider(a.provider)
      if (provider?.cacheSavingsPerToken != null) {
        let rate = 0
        if (typeof provider.cacheSavingsPerToken === 'number') {
          rate = provider.cacheSavingsPerToken
        } else {
          const modelLower = a.model.toLowerCase()
          const matchKey = Object.keys(provider.cacheSavingsPerToken)
            .sort((x, y) => y.length - x.length)
            .find(key => key !== 'default' && modelLower.includes(key))
          rate = matchKey
            ? provider.cacheSavingsPerToken[matchKey]
            : (provider.cacheSavingsPerToken['default'] ?? 0)
        }
        estimatedSavings += (a.cacheReadTokens ?? 0) * rate
      }
      
      modelCosts[a.model] = (modelCosts[a.model] ?? 0) + a.costUsd
    }
    const topModel = Object.entries(modelCosts).reduce<[string, number] | undefined>(
      (best, entry) => best == null || entry[1] > best[1] ? entry : best,
      undefined
    )
    return { totalSpend, totalInputTokens, totalOutputTokens, totalCacheRead, estimatedSavings, topModel }
  }, [aggregates])

  const priorTotalSpend = useMemo(() => priorAggregates.reduce((sum, a) => sum + a.costUsd, 0), [priorAggregates])
  const deltaUsd = totalSpend - priorTotalSpend
  const deltaGbpValue = deltaUsd * rate
  const deltaPct = priorTotalSpend > 0 ? (deltaUsd / priorTotalSpend) * 100 : null

  const unread = useMemo(() => insights.filter(i => !i.acknowledged).length, [insights])
  const totalTokens = totalInputTokens + totalOutputTokens
  // Share of prompt tokens served from cache = cacheRead / (cacheRead + fresh input).
  // (input_tokens excludes cache reads, so the denominator is the whole prompt.)
  const promptTokens = totalCacheRead + totalInputTokens
  const cacheHitRate = promptTokens > 0 ? (totalCacheRead / promptTokens) * 100 : 0

  return (
    <div className="summary-cards">
      <Card>
        <div className="card-label card-label--row">
          Spend · {AGGREGATES_DAYS_RANGE} days
          <InfoPopover id="spend-info" title={`Spend · ${AGGREGATES_DAYS_RANGE} days`}>
            <p>Rolling {AGGREGATES_DAYS_RANGE}-day window across all providers: Anthropic, Copilot, Google, and OpenAI.</p>
            <p>Copilot and Google subscription usage is shown at notional API list rates — no real money changes hands on those providers.</p>
          </InfoPopover>
        </div>
        <div className="card-value card-value--lead">{formatGbp(totalSpend, rate)}</div>
        {priorAggregates.length > 0 && (
          <div className={`card-sub card-delta${deltaGbpValue > 0.005 ? ' card-delta--up' : deltaGbpValue < -0.005 ? ' card-delta--down' : ''}`}>
            {deltaGbpValue > 0.005 ? '↑' : deltaGbpValue < -0.005 ? '↓' : '—'} {gbp(Math.abs(deltaGbpValue))}{deltaPct !== null ? ` (${Math.abs(deltaPct).toFixed(0)}%)` : ''} vs prior {AGGREGATES_DAYS_RANGE}d
          </div>
        )}
      </Card>
      <Card>
        <div className="card-label">Tokens</div>
        <div className="card-value">{totalTokens === 0 ? '—' : `${(totalTokens / 1_000_000).toFixed(1)}M`}</div>
        {totalTokens > 0 && (
          <div className="card-sub">
            <div>{formatInt(totalInputTokens)} in / {formatInt(totalOutputTokens)} out</div>
            {totalCacheRead > 0 && (
              <div style={{ marginTop: 'var(--space-1)', color: 'var(--ok-text)', fontWeight: 500, display: 'flex', alignItems: 'center', gap: 'var(--space-2)' }}>
                {(cacheHitRate / 100).toLocaleString(undefined, { style: 'percent', maximumFractionDigits: 0 })} cache hit (saved {formatGbp(estimatedSavings, rate)})
                <InfoPopover id="cache-info" title="Prompt cache">
                  <p>Cache hit: the share of prompt tokens served from the provider's cache instead of being re-read at full price.</p>
                  <p>Saved: the estimated value of those cached reads versus paying the full input list price. Notional for subscription providers — the saving is what it would be worth at API list rates.</p>
                </InfoPopover>
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
