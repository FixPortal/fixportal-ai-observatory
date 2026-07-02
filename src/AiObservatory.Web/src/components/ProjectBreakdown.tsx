import { useState, useMemo } from 'react'
import type { ProjectActivity } from '../api/client'
import { filterProjects, sortProjects } from './projectBreakdownSort'
import type { ProjectSortField, SortDirection } from './projectBreakdownSort'
import { formatActiveTime } from '../lib/duration'
import SearchIcon from '../design/SearchIcon'

interface SortableHeaderProps {
  field: ProjectSortField
  label: string
  sortField: ProjectSortField
  sortDirection: SortDirection
  onSort: (field: ProjectSortField) => void
}

const SortableHeader = ({ field, label, sortField, sortDirection, onSort }: SortableHeaderProps) => {
  const isActive = sortField === field
  let ariaSort: 'ascending' | 'descending' | 'none' = 'none'
  if (isActive) ariaSort = sortDirection === 'asc' ? 'ascending' : 'descending'
  let indicatorSymbol = '↕'
  if (isActive) indicatorSymbol = sortDirection === 'asc' ? '▲' : '▼'

  // Real <button> inside the <th>: native keyboard/focus semantics instead of a
  // tabIndex/onKeyDown-decorated cell, and the <th> keeps its column-header role.
  return (
    <th className="sortable-header" aria-sort={ariaSort}>
      <button type="button" className="sortable-header__content" onClick={() => onSort(field)}>
        {label}
        <span className={`sort-indicator ${isActive ? 'sort-indicator--active' : ''}`} aria-hidden="true">
          {indicatorSymbol}
        </span>
      </button>
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
      <div className="breakdown-controls">
        <div className="breakdown-search-container">
          <SearchIcon />
          <input
            type="text"
            placeholder="Search projects..."
            value={searchQuery}
            onChange={(e) => setSearchQuery(e.target.value)}
            className="breakdown-search"
            aria-label="Search projects"
          />
        </div>
        {selectedProject && (
          <div className="breakdown-filters">
            <button
              type="button"
              className="filter-chip"
              aria-label={`Clear filter: ${selectedProject}`}
              onClick={() => onSelectProject(null)}
            >
              Filtered: {selectedProject} ✕
            </button>
          </div>
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
                className={p.project === selectedProject ? 'project-table__row--selected' : undefined}
              >
                <td>
                  {/* Real <button> for the interactive cell: keeps the <tr>/<td> table
                      semantics intact (a <tr role="button"> flattens the row for AT). */}
                  <button
                    type="button"
                    className="project-table__select"
                    aria-pressed={p.project === selectedProject}
                    onClick={() => onSelectProject(p.project === selectedProject ? null : p.project)}
                  >
                    {p.project}
                  </button>
                </td>
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
