import { useMemo } from 'react'
import { useQuery } from '@tanstack/react-query'
import {
  getAggregates, getInsights, getSubscriptions,
  getAdversarialReviewRuns, getAdversarialReviewStats, getCavemanStats,
  getBudgetRules, getEmailStatus,
  getActivityDaily, getActivityByProject,
  type DailyAggregate, type Insight, type Subscription,
  type AdversarialReviewRun, type AdversarialReviewStats, type CavemanStats,
  type BudgetRule, type DailyActivity, type ProjectActivity,
} from './client'

// Shared query hooks. Components subscribe directly (react-query deduplicates by
// key), so data is not props-drilled from the page and each panel can resolve
// and render independently.

// Single source of truth for the dashboard window: the aggregates query, the
// spend-card label, and the chart all derive from this so they cannot drift.
export const AGGREGATES_DAYS_RANGE = 31

export const localDate = (d: Date) => d.toISOString().slice(0, 10)

const aggregatesQueryFn = () => {
  const to = new Date()
  const from = new Date(to.getTime() - (AGGREGATES_DAYS_RANGE - 1) * 24 * 60 * 60 * 1000)
  return getAggregates(localDate(from), localDate(to))
}

export function useAggregates(from?: Date, to?: Date): DailyAggregate[] {
  const hasRange = from != null && to != null
  const { data = [] } = useQuery({
    queryKey: hasRange ? ['aggregates', localDate(from!), localDate(to!)] : ['aggregates'],
    queryFn: hasRange
      ? () => getAggregates(localDate(from!), localDate(to!))
      : aggregatesQueryFn,
  })
  return data
}

export function useActivityDaily(from?: Date, to?: Date): DailyActivity[] {
  const hasRange = from != null && to != null
  const { data = [] } = useQuery({
    queryKey: hasRange ? ['activity-daily', localDate(from!), localDate(to!)] : ['activity-daily'],
    queryFn: hasRange
      ? () => getActivityDaily(localDate(from!), localDate(to!))
      : () => getActivityDaily(),
  })
  return data
}

export function useActivityByProject(from?: Date, to?: Date): ProjectActivity[] {
  const hasRange = from != null && to != null
  const { data = [] } = useQuery({
    queryKey: hasRange ? ['activity-by-project', localDate(from!), localDate(to!)] : ['activity-by-project'],
    queryFn: hasRange
      ? () => getActivityByProject(localDate(from!), localDate(to!))
      : () => getActivityByProject(),
  })
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

export function useCavemanStats(): CavemanStats | undefined {
  const { data } = useQuery({ queryKey: ['caveman-stats'], queryFn: getCavemanStats })
  return data
}

export function useBudgetRules(): { rules: BudgetRule[]; isLoading: boolean; isError: boolean } {
  const { data = [], isPending, isError } = useQuery({ queryKey: ['budget-rules'], queryFn: getBudgetRules })
  return { rules: data, isLoading: isPending, isError }
}

export function usePriorPeriodAggregates(): DailyAggregate[] {
  // Compute the window once per mount, not in the render body — keeps render pure
  // and the date objects stable. (The query key is day-granular via localDate, so
  // this never churns mid-day regardless.)
  const { priorFrom, priorTo } = useMemo(() => {
    const now = new Date()
    const to = new Date(now.getTime() - AGGREGATES_DAYS_RANGE * 24 * 60 * 60 * 1000)
    const from = new Date(to.getTime() - (AGGREGATES_DAYS_RANGE - 1) * 24 * 60 * 60 * 1000)
    return { priorFrom: from, priorTo: to }
  }, [])
  return useAggregates(priorFrom, priorTo)
}

export function useEmailStatus(): { configured: boolean | undefined } {
  const { data } = useQuery({ queryKey: ['email-status'], queryFn: getEmailStatus })
  return { configured: data?.configured }
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
