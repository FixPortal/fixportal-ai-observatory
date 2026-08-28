import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen } from '@testing-library/react'
import { afterEach, expect, test, vi } from 'vitest'
import SpendPage from './SpendPage'

vi.mock('../api/queries', async importOriginal => ({
  ...await importOriginal<typeof import('../api/queries')>(),
  useSpendCategories: () => [],
  useSpendVendors: () => [],
  useAllSpendCategories: () => [],
  useAllSpendVendors: () => [],
  useSpendEntries: () => ({ entries: [], isLoading: false, isError: false }),
}))
vi.mock('../auth/msal', () => ({ isReadonly: false }))

afterEach(() => vi.useRealTimers())

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
})
