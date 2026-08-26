import { lazy, Suspense, useMemo } from 'react'
import DateRangePicker from '../components/DateRangePicker'
import ReportingCards from '../components/ReportingCards'
import BudgetRulesPanel from '../components/BudgetRulesPanel'
import { useDateRange } from '../lib/dateRange'
import { localDate, useAllSpendVendors, useSpendEntries } from '../api/queries'
import { buildBilledDailySeries, buildBilledVendorSeries } from '../lib/billedReporting'

const BilledSpendChart = lazy(() => import('../components/BilledSpendChart'))
const BilledVendorSplit = lazy(() => import('../components/BilledVendorSplit'))

export default function ReportingPage() {
  const { from, to, preset, setPreset, setCustom } = useDateRange()
  const { entries, isLoading, isError } = useSpendEntries(from, to)
  const vendors = useAllSpendVendors()
  const daysInRange = Math.max(1, Math.round((to.getTime() - from.getTime()) / 86400000) + 1)
  const rangeLabel = `${localDate(from)} to ${localDate(to)}`
  const dailySeries = useMemo(() => buildBilledDailySeries(entries), [entries])
  const vendorSeries = useMemo(() => buildBilledVendorSeries(entries, vendors), [entries, vendors])

  if (isError) {
    return <div className="error-banner" role="alert">Couldn’t load spend. Check the API service and try refreshing.</div>
  }

  return (
    <div className="reporting-page">
      <div className="reporting-range-bar">
        <DateRangePicker from={from} to={to} preset={preset} onPreset={setPreset} onCustom={setCustom} />
        <span className="reporting-range-label">{rangeLabel}</span>
      </div>
      <ReportingCards entries={entries} vendors={vendors} daysInRange={daysInRange} />
      <div className="main-grid">
        <div className="panel">
          <div className="panel-title">Billed spend — {rangeLabel}</div>
          {isLoading ? <div className="chart-skeleton" /> : entries.length === 0 ? (
            <p className="panel-empty">No billed spend reported for this period.</p>
          ) : (
            <Suspense fallback={<div className="chart-skeleton" />}>
              <BilledSpendChart data={dailySeries} />
            </Suspense>
          )}
        </div>
        <div className="panel">
          <div className="panel-title">Billed spend by vendor</div>
          {isLoading ? <div className="chart-skeleton" /> : entries.length === 0 ? (
            <p className="panel-empty">No billed spend reported for this period.</p>
          ) : (
            <Suspense fallback={<div className="chart-skeleton" />}>
              <BilledVendorSplit data={vendorSeries} />
            </Suspense>
          )}
        </div>
      </div>
      <BudgetRulesPanel />
    </div>
  )
}
