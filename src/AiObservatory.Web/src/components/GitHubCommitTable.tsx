import { useState, useMemo } from 'react'
import type { GitHubCommitSummary } from '../api/client'
import { sortCommitSummaries } from './githubSort'
import type { CommitSortField, SortDirection } from './githubSort'

export default function GitHubCommitTable({ summary }: { summary: GitHubCommitSummary[] }) {
  const [sortField, setSortField] = useState<CommitSortField>('commitCount')
  const [sortDirection, setSortDirection] = useState<SortDirection>('desc')

  const visible = useMemo(
    () => sortCommitSummaries(summary, sortField, sortDirection),
    [summary, sortField, sortDirection],
  )

  if (summary.length === 0) return <p className="panel-empty">No commit activity for this period.</p>

  const handleSort = (field: CommitSortField) => {
    if (sortField === field) setSortDirection((prev) => (prev === 'asc' ? 'desc' : 'asc'))
    else { setSortField(field); setSortDirection('desc') }
  }

  return (
    <table className="project-table">
      <thead>
        <tr>
          <th>
            <button type="button" onClick={() => handleSort('repo')}>Repo</button>
          </th>
          <th>
            <button type="button" onClick={() => handleSort('commitCount')}>Commits</button>
          </th>
          <th>Churn</th>
        </tr>
      </thead>
      <tbody>
        {visible.map((s) => (
          <tr key={s.repo}>
            <td>{s.repo}</td>
            <td>{s.commitCount}</td>
            <td>+{s.additions} / -{s.deletions}</td>
          </tr>
        ))}
      </tbody>
    </table>
  )
}
