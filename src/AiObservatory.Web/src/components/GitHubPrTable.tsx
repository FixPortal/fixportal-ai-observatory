import { useState, useMemo } from 'react'
import type { GitHubPr } from '../api/client'
import { filterPrs, sortPrs } from './githubSort'
import type { PrSortField, SortDirection } from './githubSort'
import SearchIcon from '../design/SearchIcon'

interface SortableHeaderProps {
  field: PrSortField
  label: string
  sortField: PrSortField
  sortDirection: SortDirection
  onSort: (field: PrSortField) => void
}

const SortableHeader = ({ field, label, sortField, sortDirection, onSort }: SortableHeaderProps) => {
  const isActive = sortField === field
  let ariaSort: 'ascending' | 'descending' | 'none' = 'none'
  if (isActive) ariaSort = sortDirection === 'asc' ? 'ascending' : 'descending'
  let indicatorSymbol = '↕'
  if (isActive) indicatorSymbol = sortDirection === 'asc' ? '▲' : '▼'

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

export default function GitHubPrTable({ prs }: { prs: GitHubPr[] }) {
  const [searchQuery, setSearchQuery] = useState('')
  const [sortField, setSortField] = useState<PrSortField>('createdAt')
  const [sortDirection, setSortDirection] = useState<SortDirection>('desc')

  const visible = useMemo(
    () => sortPrs(filterPrs(prs, searchQuery), sortField, sortDirection),
    [prs, searchQuery, sortField, sortDirection],
  )

  if (prs.length === 0) return <p className="panel-empty">No PR activity for this period.</p>

  const handleSort = (field: PrSortField) => {
    if (sortField === field) setSortDirection((prev) => (prev === 'asc' ? 'desc' : 'asc'))
    else { setSortField(field); setSortDirection('desc') }
  }

  return (
    <>
      <div className="breakdown-controls">
        <div className="breakdown-search-container">
          <SearchIcon />
          <input
            type="text"
            placeholder="Search PRs..."
            value={searchQuery}
            onChange={(e) => setSearchQuery(e.target.value)}
            className="breakdown-search"
            aria-label="Search pull requests"
          />
        </div>
      </div>
      {visible.length === 0 ? (
        <p className="panel-empty">No matching PRs found.</p>
      ) : (
        <table className="model-table">
          <thead>
            <tr>
              <SortableHeader field="repo" label="Repo" sortField={sortField} sortDirection={sortDirection} onSort={handleSort} />
              <th>Title</th>
              <th>Author</th>
              <th>State</th>
              <SortableHeader field="createdAt" label="Created" sortField={sortField} sortDirection={sortDirection} onSort={handleSort} />
              <SortableHeader field="reviewCount" label="Reviews" sortField={sortField} sortDirection={sortDirection} onSort={handleSort} />
              <SortableHeader field="turnaroundHours" label="Turnaround" sortField={sortField} sortDirection={sortDirection} onSort={handleSort} />
            </tr>
          </thead>
          <tbody>
            {visible.map((p) => (
              <tr key={`${p.repo}#${p.number}`}>
                <td>{p.repo}</td>
                <td>#{p.number} {p.title}</td>
                <td>{p.author}</td>
                <td>{p.state}</td>
                <td>{new Date(p.createdAt).toLocaleDateString()}</td>
                <td>{p.reviewCount}</td>
                <td>{p.turnaroundHours == null ? '—' : `${p.turnaroundHours}h`}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </>
  )
}
