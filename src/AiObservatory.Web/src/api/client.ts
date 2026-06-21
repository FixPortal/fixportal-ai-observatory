// Prod build calls the API cross-origin (VITE_API_BASE); dev uses the Vite proxy.
import { getAccessToken } from '../auth/msal'

const BASE = (import.meta.env.VITE_API_BASE ?? '') + '/api'

/** Carries the HTTP status so the UI can tell auth failures from outages. */
export class ApiError extends Error {
  readonly status: number
  constructor(status: number, message: string) {
    super(message)
    this.name = 'ApiError'
    this.status = status
  }
}

// Auth: an Entra access token when signed in (production). In dev with no Entra
// config getAccessToken() returns null and the header is omitted — the local API
// runs without a key.
async function authHeaders(): Promise<Record<string, string>> {
  const token = await getAccessToken()
  return token ? { Authorization: `Bearer ${token}` } : {}
}

async function request(path: string, init: RequestInit = {}): Promise<Response> {
  const res = await fetch(`${BASE}${path}`, {
    ...init,
    headers: { ...(init.headers as Record<string, string>), ...(await authHeaders()) },
  })
  if (!res.ok) throw new ApiError(res.status, `${init.method ?? 'GET'} ${path} failed: ${res.status}`)
  return res
}

export interface DailyAggregate {
  date: string
  provider: 'anthropic' | 'copilot' | 'google'
  model: string
  inputTokens: number
  outputTokens: number
  cacheReadTokens: number
  cacheWriteTokens: number
  costUsd: number
  requestCount: number
}

export interface Insight {
  id: string
  generatedAt: string
  // eslint-disable-next-line sonarjs/max-union-size
  insightType: 'summary' | 'efficiency' | 'anomaly' | 'recommendation'
  title: string
  body: string
  data: Record<string, unknown>
  acknowledged: boolean
}

export interface Subscription {
  id: string
  provider: string          // 'anthropic' | 'copilot' | 'google'
  name: string
  costAmount: number
  currency: string
  billingDay: number
  activeFrom: string        // ISO date yyyy-MM-dd
  activeTo: string | null
  extraUsageCost: number | null
}

async function getJson<T>(path: string, params?: Record<string, string | undefined>): Promise<T> {
  const entries = Object.entries(params ?? {}).filter((e): e is [string, string] => e[1] != null)
  const qs = entries.length ? '?' + new URLSearchParams(entries).toString() : ''
  const res = await request(`${path}${qs}`)
  return res.json() as Promise<T>
}

const jsonHeaders = { 'Content-Type': 'application/json' }

export const getAggregates = (from?: string, to?: string) =>
  getJson<DailyAggregate[]>('/aggregates', { from, to })

export const getInsights = () => getJson<Insight[]>('/insights')

export const getSubscriptions = () => getJson<Subscription[]>('/subscriptions')

export const acknowledgeInsight = async (id: string): Promise<void> => {
  await request(`/insights/${id}/acknowledge`, { method: 'POST' })
}

export const createSubscription = async (body: Omit<Subscription, 'id'>): Promise<Subscription> => {
  const res = await request('/subscriptions', {
    method: 'POST',
    headers: jsonHeaders,
    body: JSON.stringify(body),
  })
  return res.json() as Promise<Subscription>
}

export const updateSubscription = async (id: string, body: Omit<Subscription, 'id'>): Promise<Subscription> => {
  const res = await request(`/subscriptions/${id}`, {
    method: 'PUT',
    headers: jsonHeaders,
    body: JSON.stringify(body),
  })
  return res.json() as Promise<Subscription>
}

export const patchExtraUsage = async (id: string, amount: number | null): Promise<Subscription> => {
  const res = await request(`/subscriptions/${id}/extra-usage`, {
    method: 'PATCH',
    headers: jsonHeaders,
    body: JSON.stringify({ amount }),
  })
  return res.json() as Promise<Subscription>
}

export const deleteSubscription = async (id: string): Promise<void> => {
  await request(`/subscriptions/${id}`, { method: 'DELETE' })
}

export const explainInsight = async (id: string): Promise<{ explanation: string }> => {
  const res = await request(`/insights/${id}/explain`, { method: 'POST' })
  return res.json() as Promise<{ explanation: string }>
}

export interface AdversarialReviewRun {
  id: string
  reviewer: string
  model: string
  inputTokens: number
  outputTokens: number
  costUsd: number
  reviewDurationMs: number
  issuesRaised: number
  issuesAccepted: number
  costPerAcceptedFinding: number | null
  runId: string
  recordedAt: string
}

export interface AdversarialReviewStats {
  reviewer: string
  model: string
  runCount: number
  avgCostPerRun: number
  avgIssuesRaised: number
  avgIssuesAccepted: number
  avgCostPerAcceptedFinding: number | null
}

export const getAdversarialReviewRuns = () =>
  getJson<AdversarialReviewRun[]>('/adversarial-review/runs')

export const getAdversarialReviewStats = () =>
  getJson<AdversarialReviewStats[]>('/adversarial-review/stats')

export interface CavemanStats {
  sessions: number
  totalOutputTokens: number
  totalEstSavedTokens: number
  totalEstSavedUsd: number
}

export const getCavemanStats = () => getJson<CavemanStats>('/caveman-stats')

export interface BudgetRule {
  id: string
  provider: string | null
  period: 'daily' | 'weekly' | 'monthly'
  thresholdUsd: number
  lastTriggeredAt: string | null
}

export const getBudgetRules = () => getJson<BudgetRule[]>('/budget-rules')

export const createBudgetRule = async (body: Omit<BudgetRule, 'id' | 'lastTriggeredAt'>): Promise<BudgetRule> => {
  const res = await request('/budget-rules', { method: 'POST', headers: jsonHeaders, body: JSON.stringify(body) })
  return res.json() as Promise<BudgetRule>
}

export const deleteBudgetRule = async (id: string): Promise<void> => {
  await request(`/budget-rules/${id}`, { method: 'DELETE' })
}

export const getEmailStatus = () => getJson<{ configured: boolean }>('/budget-rules/email-status')
