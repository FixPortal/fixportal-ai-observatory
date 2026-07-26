import { useState, useMemo } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import SpendFilterBar from '../components/SpendFilterBar'
import SpendTotals from '../components/SpendTotals'
import SpendLedgerTable from '../components/SpendLedgerTable'
import SpendEntryModal from '../components/SpendEntryModal'
import { useSpendCategories, useSpendVendors, useAllSpendCategories, useAllSpendVendors, useSpendEntries } from '../api/queries'
import { deleteSpendEntry } from '../api/client'
import { filterEntries, totalGbp } from '../lib/spendFilters'
import { isReadonly } from '../auth/msal'

const RANGE_DAYS = 90

export default function SpendPage() {
  const qc = useQueryClient()
  const [categoryId, setCategoryId] = useState<string | undefined>()
  const [vendorId, setVendorId] = useState<string | undefined>()
  const [adding, setAdding] = useState(false)

  // Fixed 90-day window in phase 1; the configurable date range arrives with the
  // charts in phase 2, where it earns its keep.
  const [to] = useState(() => new Date())
  const from = useMemo(() => new Date(to.getTime() - RANGE_DAYS * 86_400_000), [to])

  const categories = useSpendCategories()
  const vendors = useSpendVendors()
  // Includes archived rows: a historical entry must still resolve a display name for a
  // category or vendor that has since been retired (spec §8). Pickers stay on the live
  // lists above so a retired one cannot be selected again.
  const allCategories = useAllSpendCategories()
  const allVendors = useAllSpendVendors()
  const { entries, isLoading, isError } = useSpendEntries(from, to)

  const visible = useMemo(
    () => filterEntries(entries, { categoryId, vendorId }),
    [entries, categoryId, vendorId])

  const total = useMemo(() => totalGbp(visible), [visible])

  const largestCategory = useMemo(() => {
    if (visible.length === 0) return null
    const byCategory = new Map<string, number>()
    for (const e of visible) {
      byCategory.set(e.categoryId, (byCategory.get(e.categoryId) ?? 0) + e.amountGbp)
    }
    const [topId] = [...byCategory.entries()].sort((a, b) => b[1] - a[1])[0]
    return allCategories.find(c => c.id === topId)?.displayName ?? null
  }, [visible, allCategories])

  const remove = useMutation({
    mutationFn: deleteSpendEntry,
    onSuccess: () => qc.invalidateQueries({ queryKey: ['spend-entries'] }),
    onError: (err: Error) => alert(`Failed to delete entry: ${err.message}`),
  })

  if (isError) {
    return <div className="error-banner">Couldn’t load spend. Check the API service and try refreshing.</div>
  }

  return (
    <section className="spend-page">
      <SpendFilterBar
        categories={categories}
        vendors={vendors}
        categoryId={categoryId}
        vendorId={vendorId}
        onCategoryChange={setCategoryId}
        onVendorChange={setVendorId}
        onAddEntry={() => setAdding(true)}
        canEdit={!isReadonly}
      />

      <SpendTotals total={total} entryCount={visible.length} largestCategory={largestCategory} />

      {isLoading
        ? <p>Loading spend…</p>
        : <SpendLedgerTable
            entries={visible}
            categories={allCategories}
            vendors={allVendors}
            onDelete={id => remove.mutate(id)}
            canEdit={!isReadonly}
            isDeleting={remove.isPending}
          />}

      {adding && (
        <SpendEntryModal
          categories={categories}
          vendors={vendors}
          from={from}
          to={to}
          onClose={() => setAdding(false)}
        />
      )}
    </section>
  )
}
