import { useState, useMemo } from 'react'
import type { GitHubCiSummary } from '../api/client'
import { sortCiSummaries } from './githubSort'
import type { CiSortField, SortDirection } from './githubSort'

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
          <th><button type="button" onClick={() => handleSort('repo')}>Repo</button></th>
          <th>Workflow</th>
          <th><button type="button" onClick={() => handleSort('totalRuns')}>Runs</button></th>
          <th>Failed</th>
          <th><button type="button" onClick={() => handleSort('successRate')}>Success rate</button></th>
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
              {c.successRate.toFixed(0)}%
            </td>
          </tr>
        ))}
      </tbody>
    </table>
  )
}
