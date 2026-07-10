import { describe, expect, it } from 'vitest'
import { render, screen } from '@testing-library/react'
import GitHubCiTable from './GitHubCiTable'
import type { GitHubCiSummary } from '../api/client'

const ci: GitHubCiSummary[] = [
  { repo: 'fix-portal/a', workflowName: 'ci.yml', totalRuns: 10, failedRuns: 3, successRate: 70 },
  { repo: 'fix-portal/b', workflowName: 'ci.yml', totalRuns: 10, failedRuns: 0, successRate: 100 },
]

describe('GitHubCiTable', () => {
  it('sorts by success rate ascending by default, worst repo first', () => {
    render(<GitHubCiTable ci={ci} />)
    const rows = screen.getAllByRole('row').slice(1) // skip header row
    expect(rows[0]).toHaveTextContent('fix-portal/a')
  })

  it('shows an empty state when there is no CI activity', () => {
    render(<GitHubCiTable ci={[]} />)
    expect(screen.getByText('No CI activity for this period.')).toBeInTheDocument()
  })

  it('marks low success rates without emoji glyphs', () => {
    render(<GitHubCiTable ci={ci} />)

    expect(screen.getByText('70%')).toBeInTheDocument()
    expect(screen.queryByText(/⚠/u)).not.toBeInTheDocument()
  })
})
