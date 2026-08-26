import { lazy, Suspense } from 'react'
import DateRangePicker from '../components/DateRangePicker'
import ReportingCards from '../components/ReportingCards'
import BudgetRulesPanel from '../components/BudgetRulesPanel'
import { useDateRange } from '../lib/dateRange'
import { localDate, useBilledReporting } from '../api/queries'

const BilledSpendChart = lazy(() => import('../components/BilledSpendChart'))
const BilledVendorSplit = lazy(() => import('../components/BilledVendorSplit'))

export default function ReportingPage() {
  const { from, to, preset, setPreset, setCustom } = useDateRange()
  const { report, isLoading, isError } = useBilledReporting(from, to)
  const rangeLabel = `${localDate(from)} to ${localDate(to)}`

  if (isError) {
    return <div className="error-banner" role="alert">Couldn’t load billed reporting. Check the API service and try refreshing.</div>
  }

  return (
    <div className="reporting-page">
      <div className="reporting-range-bar">
        <DateRangePicker from={from} to={to} preset={preset} onPreset={setPreset} onCustom={setCustom} />
        <span className="reporting-range-label">{rangeLabel}</span>
      </div>
      <ReportingCards report={report} />
      <div className="main-grid">
        <div className="panel">
          <div className="panel-title">Billed spend — {rangeLabel}</div>
          {isLoading ? <div className="chart-skeleton" /> : !report || report.entryCount === 0 ? (
            <p className="panel-empty">No billed spend reported for this period.</p>
          ) : (
            <Suspense fallback={<div className="chart-skeleton" />}>
              <BilledSpendChart data={report.dailySeries} />
            </Suspense>
          )}
        </div>
        <div className="panel">
          <div className="panel-title">Billed spend by vendor</div>
          {isLoading ? <div className="chart-skeleton" /> : !report || report.entryCount === 0 ? (
            <p className="panel-empty">No billed spend reported for this period.</p>
          ) : (
            <Suspense fallback={<div className="chart-skeleton" />}>
              <BilledVendorSplit data={report.vendorSeries} />
            </Suspense>
          )}
        </div>
      </div>
      <BudgetRulesPanel />
    </div>
  )
}
