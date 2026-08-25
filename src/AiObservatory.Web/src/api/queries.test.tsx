import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { renderHook, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, test, vi } from 'vitest'
import type { ReactNode } from 'react'

const client = vi.hoisted(() => ({
  getAggregates: vi.fn().mockResolvedValue([]), getInsights: vi.fn().mockResolvedValue([]),
  getSubscriptions: vi.fn().mockResolvedValue([]), getSourceStatuses: vi.fn().mockResolvedValue([]),
  getSpendEntries: vi.fn().mockResolvedValue([]),
}))

vi.mock('./client', async importOriginal => ({ ...await importOriginal<typeof import('./client')>(), ...client }))

import { dashboardDateRange, localDate, useAggregates, useDashboardStatus, useSpendEntries } from './queries'

const wrapper = ({ children }: { children: ReactNode }) => (
  <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>{children}</QueryClientProvider>
)

beforeEach(() => {
  client.getAggregates.mockResolvedValue([])
  client.getInsights.mockResolvedValue([])
  client.getSubscriptions.mockResolvedValue([])
  client.getSourceStatuses.mockResolvedValue([])
  client.getSpendEntries.mockResolvedValue([])
})

afterEach(() => {
  vi.useRealTimers()
  vi.unstubAllEnvs()
  vi.clearAllMocks()
})

describe('dashboard queries', () => {
  test('includes source status errors in the dashboard error state', async () => {
    const sourceError = new Error('source status unavailable')
    client.getSourceStatuses.mockRejectedValueOnce(sourceError)
    const { result } = renderHook(() => useDashboardStatus(), { wrapper })
    await waitFor(() => expect(result.current.isError).toBe(true))
    expect(client.getSourceStatuses).toHaveBeenCalledOnce()
    expect(result.current.error).toBe(sourceError)
  })

  test('keeps dashboard loading until source status resolves', async () => {
    client.getSourceStatuses.mockImplementationOnce(() => new Promise(() => {}))
    const { result } = renderHook(() => useDashboardStatus(), { wrapper })
    await waitFor(() => expect(client.getSourceStatuses).toHaveBeenCalledOnce())
    expect(result.current.isLoading).toBe(true)
  })

  test('uses the shared aggregate rolling range unchanged for ledger spend', async () => {
    vi.useFakeTimers()
    vi.setSystemTime(new Date('2026-08-31T12:00:00'))
    const range = dashboardDateRange()
    renderHook(() => {
      useAggregates()
      useSpendEntries(range.from, range.to)
    }, { wrapper })
    await vi.runAllTimersAsync()
    expect(client.getAggregates).toHaveBeenCalledWith(range.from.toISOString().slice(0, 10), range.to.toISOString().slice(0, 10))
    expect(client.getSpendEntries).toHaveBeenCalledWith(range.from.toISOString().slice(0, 10), range.to.toISOString().slice(0, 10))
  })

  test('keeps all 31 local calendar dates across the BST spring transition', () => {
    vi.stubEnv('TZ', 'Europe/London')
    const range = dashboardDateRange(new Date('2026-03-30T00:30:00+01:00'))
    expect(localDate(range.from)).toBe('2026-02-28')
    expect(localDate(range.to)).toBe('2026-03-30')
  })

  test('shares the bounded aggregate and spend requests with dashboard status', async () => {
    const range = dashboardDateRange()
    const { result } = renderHook(() => {
      const status = useDashboardStatus()
      useAggregates(range.from, range.to)
      useSpendEntries(range.from, range.to)
      return status
    }, { wrapper })

    await waitFor(() => expect(result.current.isLoading).toBe(false))
    expect(client.getAggregates).toHaveBeenCalledOnce()
    expect(client.getSpendEntries).toHaveBeenCalledOnce()
    expect(client.getAggregates).toHaveBeenCalledWith(localDate(range.from), localDate(range.to))
    expect(client.getSpendEntries).toHaveBeenCalledWith(localDate(range.from), localDate(range.to))
  })

  test('includes ledger spend errors in dashboard status', async () => {
    const spendError = new Error('ledger unavailable')
    client.getSpendEntries.mockRejectedValueOnce(spendError)
    const { result } = renderHook(() => useDashboardStatus(), { wrapper })
    await waitFor(() => expect(result.current.isError).toBe(true))
    expect(result.current.error).toBe(spendError)
  })

  test('keeps dashboard loading while bounded ledger spend is pending', async () => {
    client.getSpendEntries.mockImplementationOnce(() => new Promise(() => {}))
    const { result } = renderHook(() => useDashboardStatus(), { wrapper })
    await waitFor(() => expect(client.getSpendEntries).toHaveBeenCalledOnce())
    expect(result.current.isLoading).toBe(true)
  })
})
