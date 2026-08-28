import { lazy, Suspense, useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import SpendFilterBar from '../components/SpendFilterBar'
import SpendTotals from '../components/SpendTotals'
import SpendLedgerTable from '../components/SpendLedgerTable'
import SpendEntryModal from '../components/SpendEntryModal'
import SpendCatalogModal from '../components/SpendCatalogModal'
import SpendRangeControls from '../components/SpendRangeControls'
import {
  useSpendCategories, useSpendVendors, useBilledReporting,
  useAllSpendCategories, useAllSpendVendors, useSpendEntries, invalidateSpendData,
} from '../api/queries'
import { deleteSpendEntry } from '../api/client'
import { isReadonly } from '../auth/msal'
import { useDateRange } from '../lib/dateRange'

const BilledSpendChart = lazy(() => import('../components/BilledSpendChart'))

export default function SpendPage() {
  const qc = useQueryClient()
  const [categoryId, setCategoryId] = useState<string | undefined>()
  const [vendorId, setVendorId] = useState<string | undefined>()
  const [adding, setAdding] = useState(false)
  const [managingCatalog, setManagingCatalog] = useState(false)

  // The default remains Overview's inclusive rolling 31-day window; Spend can then
  // move to calendar or arbitrary periods without changing Overview's definition.
  const {
    from, to, preset, setPreset, setCustom,
    comparisonFrom, comparisonTo, comparisonMode, setComparison, compareWithPrevious,
  } = useDateRange()

  const categories = useSpendCategories()
  const vendors = useSpendVendors()
  // Includes archived rows: a historical entry must still resolve a display name for a
  // category or vendor that has since been retired (spec §8). Pickers stay on the live
  // lists above so a retired one cannot be selected again.
  const allCategories = useAllSpendCategories()
  const allVendors = useAllSpendVendors()
  const { entries, isLoading, isError } = useSpendEntries(from, to, vendorId, categoryId)
  const primaryReporting = useBilledReporting(from, to, vendorId, categoryId)
  const comparisonReporting = useBilledReporting(comparisonFrom, comparisonTo, vendorId, categoryId)

  const total = primaryReporting.report?.totalGbp ?? 0
  const largestCategory = primaryReporting.report?.categorySeries[0]?.name ?? null
  const comparisonLabel = comparisonMode === 'previous' ? 'previous period' : 'comparison period'

  const remove = useMutation({
    mutationFn: deleteSpendEntry,
    onSuccess: () => invalidateSpendData(qc),
    onError: (err: Error) => alert(`Failed to delete entry: ${err.message}`),
  })

  if (isError || primaryReporting.isError || comparisonReporting.isError) {
    return <div className="error-banner">Couldn’t load spend. Check the API service and try refreshing.</div>
  }

  const reportingLoading = primaryReporting.isLoading || comparisonReporting.isLoading
  const bothReportsEmpty = (primaryReporting.report?.entryCount ?? 0) === 0
    && (comparisonReporting.report?.entryCount ?? 0) === 0
  let chartContent = <div className="chart-skeleton" />
  if (!reportingLoading) {
    chartContent = bothReportsEmpty ? (
      <p className="panel-empty">No billed spend reported for either period.</p>
    ) : (
      <Suspense fallback={<div className="chart-skeleton" />}>
        <BilledSpendChart
          data={primaryReporting.report?.dailySeries ?? []}
          range={{ from, to }}
          comparisonData={comparisonReporting.report?.dailySeries ?? []}
          comparisonRange={{ from: comparisonFrom, to: comparisonTo }}
          comparisonLabel={comparisonLabel}
        />
      </Suspense>
    )
  }

  return (
    <section className="spend-page">
      <SpendRangeControls
        from={from}
        to={to}
        preset={preset}
        comparisonFrom={comparisonFrom}
        comparisonTo={comparisonTo}
        comparisonMode={comparisonMode}
        onPreset={setPreset}
        onCustom={setCustom}
        onComparison={setComparison}
        onPreviousComparison={compareWithPrevious}
      />

      <SpendFilterBar
        categories={categories}
        vendors={vendors}
        categoryId={categoryId}
        vendorId={vendorId}
        onCategoryChange={setCategoryId}
        onVendorChange={setVendorId}
        onAddEntry={() => setAdding(true)}
        onManageCatalog={() => setManagingCatalog(true)}
        canEdit={!isReadonly}
      />

      {reportingLoading ? (
        <div className="spend-totals spend-totals--loading" aria-label="Loading spend totals">
          <div className="chart-skeleton" />
        </div>
      ) : (
        <SpendTotals
          total={total}
          entryCount={primaryReporting.report?.entryCount ?? 0}
          largestCategory={largestCategory}
          comparisonTotal={comparisonReporting.report?.totalGbp ?? 0}
          comparisonLabel={comparisonLabel}
        />
      )}

      <div className="panel spend-chart">
        <div className="panel-title">Billed spend over time</div>
        {chartContent}
      </div>

      {isLoading
        ? <p>Loading spend…</p>
        : <SpendLedgerTable
            entries={entries}
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

      {managingCatalog && (
        <SpendCatalogModal
          categories={allCategories}
          vendors={allVendors}
          onClose={() => setManagingCatalog(false)}
        />
      )}
    </section>
  )
}
