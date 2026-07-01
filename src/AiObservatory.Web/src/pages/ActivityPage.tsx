import { useState, lazy, Suspense } from 'react'
import DateRangePicker from '../components/DateRangePicker'
import ProjectBreakdown from '../components/ProjectBreakdown'
import { useDateRange } from '../lib/dateRange'
import { useActivityByProject, localDate } from '../api/queries'

const ActivityTrendChart = lazy(() => import('../components/ActivityTrendChart'))
const ProjectTreemap = lazy(() => import('../components/ProjectTreemap'))

export default function ActivityPage() {
  const { from, to, preset, setPreset, setCustom } = useDateRange()
  const byProject = useActivityByProject(from, to)
  const [selectedProject, setSelectedProject] = useState<string | null>(null)
  const rangeLabel = `${localDate(from)} to ${localDate(to)}`

  return (
    // Reuses the Reporting page's layout class — it's generic (flex column +
    // spacing), not Reporting-specific, so there's nothing to extract.
    <div className="reporting-page">
      <div className="reporting-range-bar">
        <DateRangePicker from={from} to={to} preset={preset} onPreset={setPreset} onCustom={setCustom} />
        <span className="reporting-range-label">{rangeLabel}</span>
      </div>
      <div className="panel">
        <div className="panel-title">Active time — {rangeLabel}</div>
        <Suspense fallback={<div className="chart-skeleton" />}>
          <ActivityTrendChart from={from} to={to} />
        </Suspense>
      </div>
      <div className="main-grid">
        <div className="panel">
          <div className="panel-title">Time by project</div>
          <ProjectBreakdown projects={byProject} selectedProject={selectedProject} onSelectProject={setSelectedProject} />
        </div>
        <div className="panel">
          <div className="panel-title">Time by project — treemap</div>
          <Suspense fallback={<div className="chart-skeleton" />}>
            <ProjectTreemap projects={byProject} selectedProject={selectedProject} onSelectProject={setSelectedProject} />
          </Suspense>
        </div>
      </div>
    </div>
  )
}
