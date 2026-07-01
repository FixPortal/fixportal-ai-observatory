import { useState, useMemo } from 'react'
import type { KeyboardEvent } from 'react'
import type { ProjectActivity } from '../api/client'
import { filterProjects, sortProjects } from './projectBreakdownSort'
import type { ProjectSortField, SortDirection } from './projectBreakdownSort'
import { formatActiveTime } from '../lib/duration'

interface SortableHeaderProps {
  field: ProjectSortField
  label: string
  sortField: ProjectSortField
  sortDirection: SortDirection
  onSort: (field: ProjectSortField) => void
}

const SortableHeader = ({ field, label, sortField, sortDirection, onSort }: SortableHeaderProps) => {
  const isActive = sortField === field
  const handleKeyDown = (e: KeyboardEvent) => {
    if (e.key === 'Enter' || e.key === ' ') {
      e.preventDefault()
      onSort(field)
    }
  }
  let ariaSort: 'ascending' | 'descending' | 'none' = 'none'
  if (isActive) ariaSort = sortDirection === 'asc' ? 'ascending' : 'descending'
  let indicatorSymbol = '↕'
  if (isActive) indicatorSymbol = sortDirection === 'asc' ? '▲' : '▼'

  return (
    <th
      onClick={() => onSort(field)}
      onKeyDown={handleKeyDown}
      className="sortable-header"
      style={{ cursor: 'pointer' }}
      tabIndex={0}
      aria-sort={ariaSort}
    >
      <span className="sortable-header__content">
        {label}
        <span className={`sort-indicator ${isActive ? 'sort-indicator--active' : ''}`} aria-hidden="true">
          {indicatorSymbol}
        </span>
      </span>
    </th>
  )
}

interface Props {
  projects: ProjectActivity[]
  selectedProject: string | null
  onSelectProject: (project: string | null) => void
}

export default function ProjectBreakdown({ projects, selectedProject, onSelectProject }: Props) {
  const [searchQuery, setSearchQuery] = useState('')
  const [sortField, setSortField] = useState<ProjectSortField>('activeSeconds')
  const [sortDirection, setSortDirection] = useState<SortDirection>('desc')

  const visible = useMemo(() => {
    const base = selectedProject ? projects.filter((p) => p.project === selectedProject) : projects
    return sortProjects(filterProjects(base, searchQuery), sortField, sortDirection)
  }, [projects, selectedProject, searchQuery, sortField, sortDirection])

  const maxActiveSeconds = useMemo(
    () => projects.reduce((m, p) => (p.activeSeconds > m ? p.activeSeconds : m), 1),
    [projects],
  )

  if (projects.length === 0) return <p className="panel-empty">No activity data for this period.</p>

  const handleSort = (field: ProjectSortField) => {
    if (sortField === field) {
      setSortDirection((prev) => (prev === 'asc' ? 'desc' : 'asc'))
    } else {
      setSortField(field)
      setSortDirection('desc')
    }
  }

  return (
    <>
      <div className="project-breakdown-controls">
        <input
          type="text"
          placeholder="Search projects..."
          value={searchQuery}
          onChange={(e) => setSearchQuery(e.target.value)}
          className="project-breakdown-search"
          aria-label="Search projects"
        />
        {selectedProject && (
          <button type="button" className="filter-chip" onClick={() => onSelectProject(null)}>
            Filtered: {selectedProject} ✕
          </button>
        )}
      </div>

      {visible.length === 0 ? (
        <p className="panel-empty">No matching projects found.</p>
      ) : (
        <table className="project-table">
          <thead>
            <tr>
              <SortableHeader field="project" label="Project" sortField={sortField} sortDirection={sortDirection} onSort={handleSort} />
              <SortableHeader field="sessions" label="Sessions" sortField={sortField} sortDirection={sortDirection} onSort={handleSort} />
              <SortableHeader field="activeSeconds" label="Active time" sortField={sortField} sortDirection={sortDirection} onSort={handleSort} />
              <th>Share</th>
            </tr>
          </thead>
          <tbody>
            {visible.map((p) => (
              <tr
                key={p.project}
                onClick={() => onSelectProject(p.project === selectedProject ? null : p.project)}
                onKeyDown={(e: KeyboardEvent) => {
                  if (e.key === 'Enter' || e.key === ' ') {
                    if (e.key === ' ') e.preventDefault()
                    onSelectProject(p.project === selectedProject ? null : p.project)
                  }
                }}
                role="button"
                tabIndex={0}
                style={{ cursor: 'pointer' }}
              >
                <td>{p.project}</td>
                <td>{p.sessionCount.toLocaleString()}</td>
                <td>{formatActiveTime(p.activeSeconds)}</td>
                <td>
                  <div className="project-table__share">
                    <div className="project-table__bar-track">
                      <div className="project-table__bar" style={{ width: `${(p.activeSeconds / maxActiveSeconds) * 100}%` }} />
                    </div>
                    <span>{p.sharePercent.toFixed(0)}%</span>
                  </div>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </>
  )
}
