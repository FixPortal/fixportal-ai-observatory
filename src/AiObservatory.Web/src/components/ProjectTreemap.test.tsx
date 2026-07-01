import { test, expect, vi } from 'vitest'
import { render, screen, fireEvent } from '@testing-library/react'
import ProjectTreemap from './ProjectTreemap'
import type { ProjectActivity } from '../api/client'

const projects: ProjectActivity[] = [
  { project: 'fixportal-ai-observatory', sessionCount: 14, activeSeconds: 24000, sharePercent: 67 },
  { project: 'Training', sessionCount: 2, activeSeconds: 2700, sharePercent: 8 },
]

test('renders a block per project', () => {
  render(<ProjectTreemap projects={projects} selectedProject={null} onSelectProject={vi.fn()} />)
  expect(screen.getByText('fixportal-ai-observatory')).toBeInTheDocument()
  expect(screen.getByText('Training')).toBeInTheDocument()
})

test('clicking a block selects that project', () => {
  const onSelectProject = vi.fn()
  render(<ProjectTreemap projects={projects} selectedProject={null} onSelectProject={onSelectProject} />)
  fireEvent.click(screen.getByText('Training'))
  expect(onSelectProject).toHaveBeenCalledWith('Training')
})

test('clicking the already-selected block deselects it', () => {
  const onSelectProject = vi.fn()
  render(<ProjectTreemap projects={projects} selectedProject="Training" onSelectProject={onSelectProject} />)
  fireEvent.click(screen.getByText('Training'))
  expect(onSelectProject).toHaveBeenCalledWith(null)
})

test('shows empty state when there is no activity', () => {
  render(<ProjectTreemap projects={[]} selectedProject={null} onSelectProject={vi.fn()} />)
  expect(screen.getByText('No activity data for this period.')).toBeInTheDocument()
})
