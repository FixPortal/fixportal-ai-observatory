import GitHubPrTable from '../components/GitHubPrTable'
import GitHubCommitTable from '../components/GitHubCommitTable'
import GitHubCiTable from '../components/GitHubCiTable'
import DateRangePicker from '../components/DateRangePicker'
import { useDateRange } from '../lib/dateRange'
import { useGitHubPrs, useGitHubCommitSummary, useGitHubCi, localDate } from '../api/queries'

export default function GitHubPage() {
  const { from, to, preset, setPreset, setCustom } = useDateRange()
  const { prs, isError: prsError, isLoading: prsLoading } = useGitHubPrs(from, to)
  const { summary, isError: summaryError, isLoading: summaryLoading } = useGitHubCommitSummary(from, to)
  const { ci, isError: ciError, isLoading: ciLoading } = useGitHubCi(from, to)
  const rangeLabel = `${localDate(from)} to ${localDate(to)}`
  const isError = prsError || summaryError || ciError

  return (
    <div className="reporting-page">
      <div className="reporting-range-bar">
        <DateRangePicker from={from} to={to} preset={preset} onPreset={setPreset} onCustom={setCustom} />
        <span className="reporting-range-label">{rangeLabel}</span>
      </div>
      {isError && (
        <div className="error-banner" role="alert">
          Couldn’t load GitHub activity data. It may be unavailable or you may not be authorised — try refreshing.
        </div>
      )}
      <div className="panel">
        <div className="panel-title">Pull requests — {rangeLabel}</div>
        <GitHubPrTable prs={prs} isError={prsError} isLoading={prsLoading} />
      </div>
      <div className="main-grid">
        <div className="panel">
          <div className="panel-title">Commits by repo</div>
          <GitHubCommitTable summary={summary} isError={summaryError} isLoading={summaryLoading} />
        </div>
        <div className="panel">
          <div className="panel-title">CI health</div>
          <GitHubCiTable ci={ci} isError={ciError} isLoading={ciLoading} />
        </div>
      </div>
    </div>
  )
}
