import { useState, useMemo } from 'react'
import type { SpendCategory, SpendEntry, SpendVendor } from '../api/client'
import { gbp, formatCurrency } from '../lib/currency'
import GitHubSortableHeader from './GitHubSortableHeader'
import type { SortDirection } from './githubSort'

type SortKey = 'occurredOn' | 'vendor' | 'category' | 'amountGbp'

interface Props {
  entries: SpendEntry[]
  categories: SpendCategory[]
  vendors: SpendVendor[]
  onDelete: (id: string) => void
  canEdit: boolean
}

/** Region 6. Sortable on any column; the rows are already filtered by SpendPage. */
export default function SpendLedgerTable({ entries, categories, vendors, onDelete, canEdit }: Props) {
  const [sortField, setSortField] = useState<SortKey>('occurredOn')
  const [sortDirection, setSortDirection] = useState<SortDirection>('desc')

  const categoryName = useMemo(
    () => new Map(categories.map(c => [c.id, c.displayName])), [categories])
  const vendorName = useMemo(
    () => new Map(vendors.map(v => [v.id, v.displayName])), [vendors])

  const sorted = useMemo(() => {
    const value = (e: SpendEntry): string | number => {
      if (sortField === 'vendor') return vendorName.get(e.vendorId) ?? ''
      if (sortField === 'category') return categoryName.get(e.categoryId) ?? ''
      if (sortField === 'amountGbp') return e.amountGbp
      return e.occurredOn
    }
    return entries.toSorted((a, b) => {
      const av = value(a), bv = value(b)
      const cmp = typeof av === 'number' && typeof bv === 'number'
        ? av - bv
        : String(av).localeCompare(String(bv))
      return sortDirection === 'asc' ? cmp : -cmp
    })
  }, [entries, sortField, sortDirection, categoryName, vendorName])

  const handleSort = (field: SortKey) => {
    if (sortField === field) setSortDirection(prev => prev === 'asc' ? 'desc' : 'asc')
    else { setSortField(field); setSortDirection('desc') }
  }

  if (entries.length === 0) {
    return <p className="spend-ledger__empty">No spend recorded for this filter.</p>
  }

  return (
    <table className="spend-ledger">
      <thead>
        <tr>
          <GitHubSortableHeader field="occurredOn" label="Date" sortField={sortField} sortDirection={sortDirection} onSort={handleSort} />
          <GitHubSortableHeader field="vendor" label="Vendor" sortField={sortField} sortDirection={sortDirection} onSort={handleSort} />
          <GitHubSortableHeader field="category" label="Category" sortField={sortField} sortDirection={sortDirection} onSort={handleSort} />
          <th>Description</th>
          <GitHubSortableHeader field="amountGbp" label="Amount" sortField={sortField} sortDirection={sortDirection} onSort={handleSort} />
          <th>Source</th>
          {canEdit && <th><span className="visually-hidden">Actions</span></th>}
        </tr>
      </thead>
      <tbody>
        {sorted.map(e => (
          <tr key={e.id}>
            <td>{e.occurredOn}</td>
            <td>{vendorName.get(e.vendorId) ?? '—'}</td>
            <td>{categoryName.get(e.categoryId) ?? '—'}</td>
            <td>{e.description ?? ''}</td>
            <td className="spend-ledger__num">
              {gbp(e.amountGbp)}
              {e.currency !== 'GBP' && (
                <span className="spend-ledger__native"> ({formatCurrency(e.amount, e.currency)})</span>
              )}
            </td>
            <td>{e.source}</td>
            {canEdit && (
              <td>
                <button type="button" onClick={() => onDelete(e.id)} aria-label={`Delete entry from ${e.occurredOn}`}>
                  Delete
                </button>
              </td>
            )}
          </tr>
        ))}
      </tbody>
    </table>
  )
}
