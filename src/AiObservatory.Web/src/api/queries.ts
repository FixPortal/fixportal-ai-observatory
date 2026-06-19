import { useQuery } from '@tanstack/react-query'
import {
  getAggregates, getInsights, getSubscriptions,
  getAdversarialReviewRuns, getAdversarialReviewStats,
  type DailyAggregate, type Insight, type Subscription,
  type AdversarialReviewRun, type AdversarialReviewStats,
} from './client'

// Shared query hooks. Components subscribe directly (react-query deduplicates by
// key), so data is not props-drilled from the page and each panel can resolve
// and render independently.

// Single source of truth for the dashboard window: the aggregates query, the
// spend-card label, and the chart all derive from this so they cannot drift.
export const AGGREGATES_DAYS_RANGE = 31

export const localDate = (d: Date) =>
  `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`

const aggregatesQueryFn = () => {
  const to = new Date()
  const from = new Date(to)
  from.setDate(from.getDate() - AGGREGATES_DAYS_RANGE)
  return getAggregates(localDate(from), localDate(to))
}

export function useAggregates(): DailyAggregate[] {
  const { data = [] } = useQuery({ queryKey: ['aggregates'], queryFn: aggregatesQueryFn })
  return data
}

export function useInsights(): Insight[] {
  const { data = [] } = useQuery({ queryKey: ['insights'], queryFn: getInsights })
  return data
}

export function useSubscriptions(): { subscriptions: Subscription[]; isError: boolean; isLoading: boolean } {
  const { data = [], isError, isPending } = useQuery({ queryKey: ['subscriptions'], queryFn: getSubscriptions })
  return { isError, subscriptions: data, isLoading: isPending }
}

export function useAdversarialReviewRuns(): AdversarialReviewRun[] {
  const { data = [] } = useQuery({ queryKey: ['adversarial-review-runs'], queryFn: getAdversarialReviewRuns })
  return data
}

export function useAdversarialReviewStats(): AdversarialReviewStats[] {
  const { data = [] } = useQuery({ queryKey: ['adversarial-review-stats'], queryFn: getAdversarialReviewStats })
  return data
}

export function useDashboardStatus(): { isError: boolean; isLoading: boolean; error: unknown } {
  const { isError: aIsError, isPending: aIsPending, error: aError } = useQuery({ queryKey: ['aggregates'], queryFn: aggregatesQueryFn })
  const { isError: iIsError, isPending: iIsPending, error: iError } = useQuery({ queryKey: ['insights'], queryFn: getInsights })
  const { isError: sIsError, isPending: sIsPending, error: sError } = useQuery({ queryKey: ['subscriptions'], queryFn: getSubscriptions })
  return {
    isError: aIsError || iIsError || sIsError,
    isLoading: aIsPending || iIsPending || sIsPending,
    error: aError ?? iError ?? sError,
  }
}
