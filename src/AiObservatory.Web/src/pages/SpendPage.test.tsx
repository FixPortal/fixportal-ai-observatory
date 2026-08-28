import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen } from '@testing-library/react'
import { afterEach, expect, test, vi } from 'vitest'
import SpendPage from './SpendPage'

const reporting = vi.hoisted(() => ({
  useBilledReporting: vi.fn((_from: Date, _to: Date, vendorId?: string) => ({
    report: {
      entryCount: 1,
      totalGbp: vendorId === 'azure-id' ? 50 : 100,
      dailyAverageGbp: 0,
      projectedMonthlyGbp: 0,
      topVendorName: null,
      topVendorGbp: null,
      dailySeries: [],
      vendorSeries: [],
      categorySeries: [{ categoryId: 'cloud-id', name: 'Cloud', amountGbp: vendorId === 'azure-id' ? 50 : 100 }],
    },
    isLoading: false,
    isError: false,
  })),
}))

vi.mock('../api/queries', async importOriginal => ({
  ...await importOriginal<typeof import('../api/queries')>(),
  useSpendCategories: () => [{ id: 'cloud-id', key: 'cloud', displayName: 'Cloud', colorVar: '', sortOrder: 1, archivedAt: null }],
  useSpendVendors: () => [{ id: 'azure-id', key: 'azure', displayName: 'Azure', provider: 'azure', defaultCategoryId: 'cloud-id', archivedAt: null }],
  useAllSpendCategories: () => [{ id: 'cloud-id', key: 'cloud', displayName: 'Cloud', colorVar: '', sortOrder: 1, archivedAt: null }],
  useAllSpendVendors: () => [{ id: 'azure-id', key: 'azure', displayName: 'Azure', provider: 'azure', defaultCategoryId: 'cloud-id', archivedAt: null }],
  useSpendEntries: () => ({ entries: [], isLoading: false, isError: false }),
  useBilledReporting: reporting.useBilledReporting,
}))
vi.mock('../auth/msal', () => ({ isReadonly: false }))

afterEach(() => {
  vi.useRealTimers()
  vi.clearAllMocks()
})

test('shows the same rolling 31-day window as Overview', () => {
  vi.useFakeTimers()
  vi.setSystemTime(new Date('2026-08-28T12:00:00'))
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })

  render(
    <QueryClientProvider client={queryClient}>
      <SpendPage />
    </QueryClientProvider>,
  )

  expect(screen.getByText('29 Jul 2026 – 28 Aug 2026')).toBeInTheDocument()
  expect(screen.getByText('28 Jun 2026 – 28 Jul 2026')).toBeInTheDocument()
})

test('offers calendar periods and updates the default comparison period', () => {
  vi.useFakeTimers()
  vi.setSystemTime(new Date('2026-08-28T12:00:00'))
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })

  render(
    <QueryClientProvider client={queryClient}>
      <SpendPage />
    </QueryClientProvider>,
  )

  expect(screen.getByRole('button', { name: 'This month' })).toBeInTheDocument()
  expect(screen.getByRole('button', { name: 'Last month' })).toBeInTheDocument()
  expect(screen.getByRole('button', { name: 'This quarter' })).toBeInTheDocument()

  fireEvent.click(screen.getByRole('button', { name: 'This month' }))

  expect(screen.getByText('01 Aug 2026 – 28 Aug 2026')).toBeInTheDocument()
  expect(screen.getByText('01 Jul 2026 – 31 Jul 2026')).toBeInTheDocument()
})

test('allows arbitrary selected and comparison dates', () => {
  vi.useFakeTimers()
  vi.setSystemTime(new Date('2026-08-28T12:00:00'))
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })

  render(
    <QueryClientProvider client={queryClient}>
      <SpendPage />
    </QueryClientProvider>,
  )

  fireEvent.change(screen.getByLabelText('Selected from'), { target: { value: '2026-04-01' } })
  fireEvent.change(screen.getByLabelText('Selected to'), { target: { value: '2026-06-30' } })
  fireEvent.change(screen.getByLabelText('Compare from'), { target: { value: '2026-01-01' } })
  fireEvent.change(screen.getByLabelText('Compare to'), { target: { value: '2026-03-31' } })

  expect(screen.getByText('01 Apr 2026 – 30 Jun 2026')).toBeInTheDocument()
  expect(screen.getByText('01 Jan 2026 – 31 Mar 2026')).toBeInTheDocument()
  expect(screen.getByText('vs comparison period')).toBeInTheDocument()
})

test('applies the vendor filter to the authoritative totals', () => {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  render(
    <QueryClientProvider client={queryClient}>
      <SpendPage />
    </QueryClientProvider>,
  )

  expect(screen.getByText('£100.00')).toBeInTheDocument()
  fireEvent.change(screen.getByLabelText('Vendor'), { target: { value: 'azure-id' } })
  expect(screen.getByText('£50.00')).toBeInTheDocument()
})

test('shows a billed-spend comparison chart', () => {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  render(
    <QueryClientProvider client={queryClient}>
      <SpendPage />
    </QueryClientProvider>,
  )

  expect(screen.getByText('Billed spend over time')).toBeInTheDocument()
})
