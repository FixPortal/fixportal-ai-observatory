import { useMemo } from 'react'
import { useQuery } from '@tanstack/react-query'
import {
  getAggregates, getInsights, getSubscriptions,
  getAdversarialReviewRuns, getAdversarialReviewStats, getCavemanStats,
  getBudgetRules, getEmailStatus,
  getActivityDaily, getActivityByProject,
  getGitHubPrs, getGitHubCommitSummary, getGitHubCi,
  getSpendCategories, getSpendVendors, getSpendEntries, getBilledReporting, getSourceStatuses,
  type DailyAggregate, type Insight, type Subscription,
  type AdversarialReviewRun, type AdversarialReviewStats, type CavemanStats,
  type BudgetRule, type DailyActivity, type ProjectActivity,
  type GitHubPr, type GitHubCommitSummary, type GitHubCiSummary,
  type SpendCategory, type SpendVendor, type SpendEntry, type BilledReporting, type SourceStatusResponse,
} from './client'

// Shared query hooks. Components subscribe directly (react-query deduplicates by
// key), so data is not props-drilled from the page and each panel can resolve
// and render independently.

// Single source of truth for the dashboard window: the aggregates query, the
// spend-card label, and the chart all derive from this so they cannot drift.
export const AGGREGATES_DAYS_RANGE = 31

// Local calendar date (yyyy-MM-dd) from the machine's timezone — NOT toISOString(),
// which emits the UTC date and, in the 00:00–00:59 local window under a positive offset
// (e.g. BST), reports yesterday. That off-by-one drove the billing-period start, the
// active-subscription filter, and the range labels a day early. One helper, every consumer.
export const localDate = (d: Date) => {
  const y = d.getFullYear()
  const m = String(d.getMonth() + 1).padStart(2, '0')
  const day = String(d.getDate()).padStart(2, '0')
  return `${y}-${m}-${day}`
}

export const dashboardDateRange = (to = new Date()) => {
  const from = new Date(to)
  from.setDate(from.getDate() - (AGGREGATES_DAYS_RANGE - 1))
  return { from, to: new Date(to) }
}

const aggregatesQueryFn = () => {
  const { from, to } = dashboardDateRange()
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

export function useActivityDaily(from?: Date, to?: Date): { daily: DailyActivity[]; isError: boolean; isLoading: boolean } {
  const hasRange = from != null && to != null
  const { data = [], isError, isPending } = useQuery({
    queryKey: hasRange ? ['activity-daily', localDate(from!), localDate(to!)] : ['activity-daily'],
    queryFn: hasRange
      ? () => getActivityDaily(localDate(from!), localDate(to!))
      : () => getActivityDaily(),
  })
  return { daily: data, isError, isLoading: isPending }
}

export function useActivityByProject(from?: Date, to?: Date): { projects: ProjectActivity[]; isError: boolean; isLoading: boolean } {
  const hasRange = from != null && to != null
  const { data = [], isError, isPending } = useQuery({
    queryKey: hasRange ? ['activity-by-project', localDate(from!), localDate(to!)] : ['activity-by-project'],
    queryFn: hasRange
      ? () => getActivityByProject(localDate(from!), localDate(to!))
      : () => getActivityByProject(),
  })
  return { projects: data, isError, isLoading: isPending }
}

export function useGitHubPrs(from?: Date, to?: Date): { prs: GitHubPr[]; isError: boolean; isLoading: boolean } {
  const hasRange = from != null && to != null
  const { data = [], isError, isPending } = useQuery({
    queryKey: hasRange ? ['github-prs', localDate(from!), localDate(to!)] : ['github-prs'],
    queryFn: hasRange ? () => getGitHubPrs(localDate(from!), localDate(to!)) : () => getGitHubPrs(),
  })
  return { prs: data, isError, isLoading: isPending }
}

export function useGitHubCommitSummary(from?: Date, to?: Date): { summary: GitHubCommitSummary[]; isError: boolean; isLoading: boolean } {
  const hasRange = from != null && to != null
  const { data = [], isError, isPending } = useQuery({
    queryKey: hasRange ? ['github-commits-summary', localDate(from!), localDate(to!)] : ['github-commits-summary'],
    queryFn: hasRange ? () => getGitHubCommitSummary(localDate(from!), localDate(to!)) : () => getGitHubCommitSummary(),
  })
  return { summary: data, isError, isLoading: isPending }
}

export function useGitHubCi(from?: Date, to?: Date): { ci: GitHubCiSummary[]; isError: boolean; isLoading: boolean } {
  const hasRange = from != null && to != null
  const { data = [], isError, isPending } = useQuery({
    queryKey: hasRange ? ['github-ci', localDate(from!), localDate(to!)] : ['github-ci'],
    queryFn: hasRange ? () => getGitHubCi(localDate(from!), localDate(to!)) : () => getGitHubCi(),
  })
  return { ci: data, isError, isLoading: isPending }
}

export function useInsights(): Insight[] {
  const { data = [] } = useQuery({ queryKey: ['insights'], queryFn: getInsights })
  return data
}

export function useSubscriptions(): { subscriptions: Subscription[]; isError: boolean; isLoading: boolean } {
  const { data = [], isError, isPending } = useQuery({ queryKey: ['subscriptions'], queryFn: getSubscriptions })
  return { isError, subscriptions: data, isLoading: isPending }
}

export function useSourceStatuses(): { statuses: SourceStatusResponse[]; isError: boolean; isLoading: boolean } {
  const { data = [], isError, isPending } = useQuery({ queryKey: ['source-statuses'], queryFn: getSourceStatuses })
  return { statuses: data, isError, isLoading: isPending }
}

export function useAdversarialReviewRuns(): { runs: AdversarialReviewRun[]; isError: boolean; isLoading: boolean } {
  const { data = [], isError, isPending } = useQuery({ queryKey: ['adversarial-review-runs'], queryFn: getAdversarialReviewRuns })
  return { runs: data, isError, isLoading: isPending }
}

export function useAdversarialReviewStats(): { stats: AdversarialReviewStats[]; isError: boolean; isLoading: boolean } {
  const { data = [], isError, isPending } = useQuery({ queryKey: ['adversarial-review-stats'], queryFn: getAdversarialReviewStats })
  return { stats: data, isError, isLoading: isPending }
}

export function useCavemanStats(): { stats: CavemanStats | undefined; isError: boolean; isLoading: boolean } {
  const { data, isError, isPending } = useQuery({ queryKey: ['caveman-stats'], queryFn: getCavemanStats })
  return { stats: data, isError, isLoading: isPending }
}

export function useBudgetRules(): { rules: BudgetRule[]; isLoading: boolean; isError: boolean } {
  const { data = [], isPending, isError } = useQuery({ queryKey: ['budget-rules'], queryFn: getBudgetRules })
  return { rules: data, isLoading: isPending, isError }
}

export function useEmailStatus(): { configured: boolean | undefined } {
  const { data } = useQuery({ queryKey: ['email-status'], queryFn: getEmailStatus })
  return { configured: data?.configured }
}

// Live only — the pickers (SpendFilterBar, SpendEntryModal). A retired category or
// vendor must not be selectable again.
export function useSpendCategories(): SpendCategory[] {
  const { data = [] } = useQuery({ queryKey: ['spend-categories'], queryFn: () => getSpendCategories() })
  return data
}

export function useSpendVendors(): SpendVendor[] {
  const { data = [] } = useQuery({ queryKey: ['spend-vendors'], queryFn: () => getSpendVendors() })
  return data
}

// Includes archived rows, so a historical ledger entry can still resolve the display
// name of a category/vendor that has since been retired (spec §8) — used for the
// ledger table's name maps and SpendPage's largestCategory, never for a picker.
export function useAllSpendCategories(): SpendCategory[] {
  const { data = [] } = useQuery({ queryKey: ['spend-categories', 'all'], queryFn: () => getSpendCategories(true) })
  return data
}

export function useAllSpendVendors(): SpendVendor[] {
  const { data = [] } = useQuery({ queryKey: ['spend-vendors', 'all'], queryFn: () => getSpendVendors(true) })
  return data
}

export function useSpendEntries(from: Date, to: Date): {
  entries: SpendEntry[]
  isLoading: boolean
  isError: boolean
} {
  const { data = [], isPending, isError } = useQuery({
    queryKey: ['spend-entries', localDate(from), localDate(to)],
    queryFn: () => getSpendEntries(localDate(from), localDate(to)),
  })
  return { entries: data, isLoading: isPending, isError }
}

export function useBilledReporting(from: Date, to: Date): {
  report: BilledReporting | undefined
  isLoading: boolean
  isError: boolean
} {
  const { data, isPending, isError } = useQuery({
    queryKey: ['billed-reporting', localDate(from), localDate(to)],
    queryFn: () => getBilledReporting(localDate(from), localDate(to)),
  })
  return { report: data, isLoading: isPending, isError }
}

export function useDashboardStatus(): { isError: boolean; isLoading: boolean; error: unknown } {
  const range = useMemo(() => dashboardDateRange(), [])
  const from = localDate(range.from)
  const to = localDate(range.to)
  const { isError: aIsError, isPending: aIsPending, error: aError } = useQuery({ queryKey: ['aggregates', from, to], queryFn: () => getAggregates(from, to) })
  const { isError: pIsError, isPending: pIsPending, error: pError } = useQuery({ queryKey: ['billed-reporting', from, to], queryFn: () => getBilledReporting(from, to) })
  const { isError: iIsError, isPending: iIsPending, error: iError } = useQuery({ queryKey: ['insights'], queryFn: getInsights })
  const { isError: sIsError, isPending: sIsPending, error: sError } = useQuery({ queryKey: ['subscriptions'], queryFn: getSubscriptions })
  const { isError: ssIsError, isPending: ssIsPending, error: ssError } = useQuery({ queryKey: ['source-statuses'], queryFn: getSourceStatuses })
  const states = [
    { isError: aIsError, isPending: aIsPending, error: aError },
    { isError: pIsError, isPending: pIsPending, error: pError },
    { isError: iIsError, isPending: iIsPending, error: iError },
    { isError: sIsError, isPending: sIsPending, error: sError },
    { isError: ssIsError, isPending: ssIsPending, error: ssError },
  ]
  return {
    isError: states.some(state => state.isError),
    isLoading: states.some(state => state.isPending),
    error: states.find(state => state.error != null)?.error,
  }
}
