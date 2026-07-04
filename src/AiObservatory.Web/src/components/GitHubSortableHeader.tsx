import type { SortDirection } from './githubSort'

interface SortableHeaderProps<T extends string> {
  field: T
  label: string
  sortField: T
  sortDirection: SortDirection
  onSort: (field: T) => void
}

export default function GitHubSortableHeader<T extends string>({
  field,
  label,
  sortField,
  sortDirection,
  onSort,
}: SortableHeaderProps<T>) {
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
