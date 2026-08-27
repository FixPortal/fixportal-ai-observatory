import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen } from '@testing-library/react'
import { describe, expect, test, vi } from 'vitest'
import type { Subscription } from '../api/client'
import SubscriptionPanel from './SubscriptionPanel'

const annualSubscription = {
  id: 'google-one',
  provider: 'google',
  name: 'Google One',
  costAmount: 189.99,
  currency: 'GBP',
  billingInterval: 'annual',
  billingMonth: 7,
  billingDay: 2,
  activeFrom: '2026-07-02',
  activeTo: null,
  extraUsageCost: null,
} as unknown as Subscription

vi.mock('../api/queries', () => ({
  useSubscriptions: () => ({ subscriptions: [annualSubscription], isError: false, isLoading: false }),
  useAggregates: () => [{
    provider: 'google', date: '2026-08-01', costBasis: 'notional', costUsd: 0,
    requestCount: 7, unknownCostCount: 7,
  }],
  localDate: () => '2026-08-27',
}))

vi.mock('../lib/currency', () => ({
  useUsdToGbp: () => 1,
  formatCurrency: (amount: number) => `£${amount.toFixed(2)}`,
  gbp: (amount: number) => `£${amount.toFixed(2)}`,
}))

vi.mock('../auth/msal', () => ({ isReadonly: true }))

describe('SubscriptionPanel', () => {
  test('renders annual price and renewal date', () => {
    render(
      <QueryClientProvider client={new QueryClient()}>
        <SubscriptionPanel />
      </QueryClientProvider>,
    )

    expect(screen.getByText('/yr')).toBeInTheDocument()
    expect(screen.getByText('Renews annually on 2 July')).toBeInTheDocument()
  })

  test('reports activity without inventing a zero monetary value', () => {
    render(
      <QueryClientProvider client={new QueryClient()}>
        <SubscriptionPanel />
      </QueryClientProvider>,
    )

    expect(screen.getByText('Not reported')).toBeInTheDocument()
    expect(screen.getByText('7 requests recorded · value not reported')).toBeInTheDocument()
    expect(screen.queryByText('£0.00')).not.toBeInTheDocument()
  })
})
