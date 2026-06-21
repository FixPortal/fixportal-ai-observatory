import { lazy, Suspense } from 'react'
import DateRangePicker from '../components/DateRangePicker'
import ReportingCards from '../components/ReportingCards'
import BudgetRulesPanel from '../components/BudgetRulesPanel'
import { useDateRange } from '../lib/dateRange'
import { useAggregates, localDate } from '../api/queries'

const SpendChart = lazy(() => import('../components/SpendChart'))
const ProviderSplit = lazy(() => import('../components/ProviderSplit'))

export default function ReportingPage() {
  const { from, to, preset, setPreset, setCustom } = useDateRange()
  const aggregates = useAggregates(from, to)
  const daysInRange = Math.max(1, Math.round((to.getTime() - from.getTime()) / 86400000) + 1)
  const rangeLabel = `${localDate(from)} to ${localDate(to)}`

  return (
    <div className="reporting-page">
      <div className="reporting-range-bar">
        <DateRangePicker from={from} to={to} preset={preset} onPreset={setPreset} onCustom={setCustom} />
        <span className="reporting-range-label">{rangeLabel}</span>
      </div>
      <ReportingCards aggregates={aggregates} daysInRange={daysInRange} />
      <div className="main-grid">
        <div className="panel">
          <div className="panel-title">Daily spend — {rangeLabel}</div>
          <Suspense fallback={<div className="chart-skeleton" />}>
            <SpendChart from={from} to={to} />
          </Suspense>
        </div>
        <div className="panel">
          <div className="panel-title">Provider split</div>
          <Suspense fallback={<div className="chart-skeleton" />}>
            <ProviderSplit from={from} to={to} />
          </Suspense>
        </div>
      </div>
      <BudgetRulesPanel />
    </div>
  )
}
