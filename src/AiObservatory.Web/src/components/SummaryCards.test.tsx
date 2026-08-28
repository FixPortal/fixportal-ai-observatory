import { fireEvent, render, screen } from '@testing-library/react'
import { beforeEach, describe, expect, test, vi } from 'vitest'
import type { BilledReporting, DailyAggregate, Insight } from '../api/client'
import SummaryCards from './SummaryCards'

const data = vi.hoisted(() => ({
  aggregates: [] as DailyAggregate[],
  insights: [] as Insight[],
  billedReporting: undefined as BilledReporting | undefined,
}))

vi.mock('../api/queries', () => ({
  AGGREGATES_DAYS_RANGE: 31,
  useAggregates: () => data.aggregates,
  useInsights: () => data.insights,
  useBilledReporting: () => ({ report: data.billedReporting, isLoading: false, isError: false }),
  dashboardDateRange: () => ({ from: new Date('2026-08-01T12:00:00'), to: new Date('2026-08-31T12:00:00') }),
}))

vi.mock('../lib/currency', () => ({ useUsdToGbp: () => 0.8, formatGbp: (usd: number, rate: number) => `£${(usd * rate).toFixed(2)}`, gbp: (amount: number) => `£${amount.toFixed(2)}` }))

const aggregate = (overrides: Partial<DailyAggregate>): DailyAggregate => ({
  date: '2026-08-24', provider: 'openai', model: 'gpt-5', sourceId: 'openai-usage-api',
  sourceKind: 'providerApi', usageScope: 'api', costBasis: 'none', inputTokens: 0,
  outputTokens: 0, cacheReadTokens: 0, cacheWriteTokens: 0, cacheWrite1hTokens: 0,
  costUsd: 0, unknownCostCount: 0, cacheSavingsUsd: 0, unknownCacheSavingsCount: 0,
  requestCount: 0, ...overrides,
})

beforeEach(() => {
  data.aggregates = [
    aggregate({ costBasis: 'listPriceEstimate', costUsd: 2, inputTokens: 100, cacheReadTokens: 50, requestCount: 1, unknownCacheSavingsCount: 1 }),
    aggregate({ costBasis: 'providerEstimated', costUsd: 3, requestCount: 1 }),
    aggregate({ costBasis: 'notional', costUsd: 4, requestCount: 1 }),
  ]
  data.billedReporting = {
    entryCount: 5003,
    totalGbp: 8,
    dailyAverageGbp: 8 / 31,
    projectedMonthlyGbp: 8 / 31 * 30,
    topVendorName: 'Anthropic',
    topVendorGbp: 8,
    dailySeries: [],
    vendorSeries: [],
    categorySeries: [],
  }
  data.insights = []
})

describe('SummaryCards', () => {
  test('renders six separated truth-basis cards with billed spend as the lead', () => {
    const { container } = render(<SummaryCards />)

    expect(screen.getByText('Billed spend · 31 days')).toBeInTheDocument()
    expect(screen.getByText('List-price estimate')).toBeInTheDocument()
    expect(screen.getByText('Provider estimate')).toBeInTheDocument()
    expect(screen.getByText('Subscription notional')).toBeInTheDocument()
    expect(screen.getByText('Tokens')).toBeInTheDocument()
    expect(screen.getByText('New insights')).toBeInTheDocument()
    expect(screen.getAllByText('USD basis; shown in GBP when reported')).toHaveLength(3)
    expect(container.querySelector('.card-value--lead')?.textContent).toBe('£8.00')
    expect(screen.getByText('£2.40')).toBeInTheDocument()
    expect(screen.getByText('£3.20')).toBeInTheDocument()
  })

  test('uses Not reported for absent money and unknown server savings', () => {
    data.billedReporting = { ...data.billedReporting!, entryCount: 0, totalGbp: 0 }
    render(<SummaryCards />)

    expect(screen.getAllByText('Not reported').length).toBeGreaterThanOrEqual(1)
    expect(screen.getByText(/Cache savings: Not reported/)).toBeInTheDocument()
    expect(screen.getByText('1 savings observation not reported')).toBeInTheDocument()
  })

  test('counts cached input in token totals and input display', () => {
    data.aggregates = [aggregate({
      inputTokens: 1_000_000,
      outputTokens: 2_000_000,
      cacheReadTokens: 3_000_000,
      cacheWriteTokens: 4_000_000,
    })]

    render(<SummaryCards />)

    expect(screen.getByText('10.0M')).toBeInTheDocument()
    expect(screen.getByText('8,000,000 in / 2,000,000 out')).toBeInTheDocument()
    expect(screen.getByText('38% cache hit')).toBeInTheDocument()
  })

  test('does not render the former blended spend, savings claim, or money comparisons', () => {
    render(<SummaryCards />)

    expect(screen.queryByText(/^Spend ·/)).not.toBeInTheDocument()
    expect(screen.queryByText(/saved £/i)).not.toBeInTheDocument()
    expect(screen.queryByText(/vs prior/i)).not.toBeInTheDocument()
    expect(screen.queryByText(/Top model/i)).not.toBeInTheDocument()
  })

  test('opens every financial popover within the summary structure', () => {
    render(<SummaryCards />)

    for (const title of ['Billed spend', 'List-price estimate', 'Provider estimate', 'Subscription notional']) {
      fireEvent.click(screen.getByRole('button', { name: title }))
      expect(screen.getByRole('region', { name: title })).toHaveClass('info-popover', 'info-popover--summary')
    }
  })
})
