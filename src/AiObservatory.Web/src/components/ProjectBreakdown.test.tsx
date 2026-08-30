import { render, screen, within } from '@testing-library/react'
import { expect, test, vi } from 'vitest'
import ProjectBreakdown from './ProjectBreakdown'

test('shows project comparison values in a bordered filterable table', () => {
  render(
    <ProjectBreakdown
      projects={[
        { project: 'fixportal-ai-observatory', sessionCount: 4, activeSeconds: 3600, sharePercent: 100 },
      ]}
      comparisonProjects={[
        { project: 'fixportal-ai-observatory', sessionCount: 2, activeSeconds: 1800, sharePercent: 75 },
        { project: 'archived-project', sessionCount: 1, activeSeconds: 600, sharePercent: 25 },
      ]}
      selectedProject={null}
      onSelectProject={vi.fn()}
    />,
  )

  expect(screen.getByRole('group', { name: 'Project filters' })).toBeInTheDocument()
  expect(within(screen.getByRole('row', { name: /fixportal-ai-observatory/i })).getByText('30m')).toBeInTheDocument()
  expect(within(screen.getByRole('row', { name: /fixportal-ai-observatory/i })).getByText('+100%')).toBeInTheDocument()
  expect(within(screen.getByRole('row', { name: /archived-project/i })).getByText('-100%')).toBeInTheDocument()
})
