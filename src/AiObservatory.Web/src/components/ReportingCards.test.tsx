import { render, screen } from '@testing-library/react'
import { expect, test } from 'vitest'
import ReportingCards from './ReportingCards'

test('keeps an incomplete top-vendor projection truthful instead of formatting null as money', () => {
  render(<ReportingCards report={{
    entryCount: 1,
    totalGbp: 10,
    dailyAverageGbp: 10,
    projectedMonthlyGbp: 300,
    topVendorName: null,
    topVendorGbp: null,
    dailySeries: [],
    vendorSeries: [],
  }} />)

  const card = screen.getByText('Top vendor').parentElement!
  expect(card).toHaveTextContent('—')
  expect(card).not.toHaveTextContent('£0.00')
})
