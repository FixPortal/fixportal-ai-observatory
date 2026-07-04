import { useState, useMemo } from 'react'
import type { GitHubCiSummary } from '../api/client'
import { sortCiSummaries } from './githubSort'
import type { CiSortField, SortDirection } from './githubSort'
import GitHubSortableHeader from './GitHubSortableHeader'

const SUCCESS_RATE_WARN_THRESHOLD = 80

export default function GitHubCiTable({ ci }: { ci: GitHubCiSummary[] }) {
  const [sortField, setSortField] = useState<CiSortField>('successRate')
  const [sortDirection, setSortDirection] = useState<SortDirection>('asc')

  const visible = useMemo(
    () => sortCiSummaries(ci, sortField, sortDirection),
    [ci, sortField, sortDirection],
  )

  if (ci.length === 0) return <p className="panel-empty">No CI activity for this period.</p>

  const handleSort = (field: CiSortField) => {
    if (sortField === field) setSortDirection((prev) => (prev === 'asc' ? 'desc' : 'asc'))
    else { setSortField(field); setSortDirection('asc') }
  }

  return (
    <table className="project-table">
      <thead>
        <tr>
          <GitHubSortableHeader field="repo" label="Repo" sortField={sortField} sortDirection={sortDirection} onSort={handleSort} />
          <th>Workflow</th>
          <GitHubSortableHeader field="totalRuns" label="Runs" sortField={sortField} sortDirection={sortDirection} onSort={handleSort} />
          <th>Failed</th>
          <GitHubSortableHeader field="successRate" label="Success rate" sortField={sortField} sortDirection={sortDirection} onSort={handleSort} />
        </tr>
      </thead>
      <tbody>
        {visible.map((c) => (
          <tr key={`${c.repo}:${c.workflowName}`}>
            <td>{c.repo}</td>
            <td>{c.workflowName}</td>
            <td>{c.totalRuns}</td>
            <td>{c.failedRuns}</td>
            <td style={{ color: c.successRate < SUCCESS_RATE_WARN_THRESHOLD ? 'var(--danger, #d33)' : undefined }}>
              {c.successRate < SUCCESS_RATE_WARN_THRESHOLD ? '⚠ ' : ''}{c.successRate.toFixed(0)}%
            </td>
          </tr>
        ))}
      </tbody>
    </table>
  )
}
