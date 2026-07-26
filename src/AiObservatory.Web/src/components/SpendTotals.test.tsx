import { describe, it, expect } from 'vitest'
import { render, screen } from '@testing-library/react'
import SpendTotals from './SpendTotals'

describe('SpendTotals', () => {
  it('shows the filtered total in GBP', () => {
    render(<SpendTotals total={412.8} entryCount={14} largestCategory="Subscriptions" />)
    expect(screen.getByText('£412.80')).toBeInTheDocument()
  })

  it('shows the entry count', () => {
    render(<SpendTotals total={412.8} entryCount={14} largestCategory="Subscriptions" />)
    expect(screen.getByText('14')).toBeInTheDocument()
  })

  it('renders a dash rather than a category name when nothing is in range', () => {
    render(<SpendTotals total={0} entryCount={0} largestCategory={null} />)
    expect(screen.getByText('£0.00')).toBeInTheDocument()
    expect(screen.getByText('—')).toBeInTheDocument()
  })
})
