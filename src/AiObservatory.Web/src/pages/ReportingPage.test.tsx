import { render, screen } from '@testing-library/react'
import { expect, test, vi } from 'vitest'
import ReportingPage from './ReportingPage'

vi.mock('../api/queries', () => ({
  localDate: () => '2026-08-01',
  useBilledReporting: () => ({ report: null, isLoading: false, isError: true }),
}))
vi.mock('../components/ReportingCards', () => ({ default: () => null }))
vi.mock('../components/BudgetRulesPanel', () => ({ default: () => null }))
vi.mock('../components/BilledSpendChart', () => ({ default: () => null }))
vi.mock('../components/BilledVendorSplit', () => ({ default: () => null }))

test('shows one honest failure state when authoritative billed reporting cannot load', () => {
  render(<ReportingPage />)

  expect(screen.getByRole('alert')).toHaveTextContent('Couldn’t load billed reporting')
})
