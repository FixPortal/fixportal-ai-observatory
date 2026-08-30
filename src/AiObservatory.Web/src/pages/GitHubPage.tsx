import { useState } from 'react'
import GitHubPrTable from '../components/GitHubPrTable'
import GitHubCommitTable from '../components/GitHubCommitTable'
import GitHubCiTable from '../components/GitHubCiTable'
import SpendRangeControls from '../components/SpendRangeControls'
import { useDateRange } from '../lib/dateRange'
import { useGitHubPrs, useGitHubCommitSummary, useGitHubCi, localDate } from '../api/queries'

export default function GitHubPage() {
  const [repo, setRepo] = useState('')
  const {
    from, to, preset, setPreset, setCustom,
    comparisonFrom, comparisonTo, comparisonMode, setComparison, compareWithPrevious,
  } = useDateRange()
  const { prs, isError: prsError, isLoading: prsLoading } = useGitHubPrs(from, to)
  const { summary, isError: summaryError, isLoading: summaryLoading } = useGitHubCommitSummary(from, to)
  const { ci, isError: ciError, isLoading: ciLoading } = useGitHubCi(from, to)
  const previousPrs = useGitHubPrs(comparisonFrom, comparisonTo)
  const previousSummary = useGitHubCommitSummary(comparisonFrom, comparisonTo)
  const previousCi = useGitHubCi(comparisonFrom, comparisonTo)
  const rangeLabel = `${localDate(from)} to ${localDate(to)}`
  const comparisonLabel = comparisonMode === 'previous' ? 'Previous period' : 'Comparison period'
  const isError = [prsError, summaryError, ciError, previousPrs.isError, previousSummary.isError, previousCi.isError].some(Boolean)
  const repos = [...new Set([
    ...prs, ...summary, ...ci, ...previousPrs.prs, ...previousSummary.summary, ...previousCi.ci,
  ].map(item => item.repo))].sort()
  const activeRepo = repos.includes(repo) ? repo : ''
  const visiblePrs = activeRepo ? prs.filter(item => item.repo === activeRepo) : prs
  const visibleSummary = activeRepo ? summary.filter(item => item.repo === activeRepo) : summary
  const visibleCi = activeRepo ? ci.filter(item => item.repo === activeRepo) : ci
  const comparisonPrs = activeRepo ? previousPrs.prs.filter(item => item.repo === activeRepo) : previousPrs.prs
  const comparisonSummary = activeRepo ? previousSummary.summary.filter(item => item.repo === activeRepo) : previousSummary.summary
  const comparisonCi = activeRepo ? previousCi.ci.filter(item => item.repo === activeRepo) : previousCi.ci
  const commits = visibleSummary.reduce((total, item) => total + item.commitCount, 0)
  const previousCommits = comparisonSummary.reduce((total, item) => total + item.commitCount, 0)
  const ciRate = successRate(visibleCi)
  const previousCiRate = successRate(comparisonCi)

  return (
    <div className="reporting-page">
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
      <div className="filter-row github-filters" role="group" aria-label="GitHub filters">
        <label className="filter-row__field">
          <span>Repo</span>
          <select value={activeRepo} onChange={event => setRepo(event.target.value)}>
            <option value="">All repos</option>
            {repos.map(name => <option key={name} value={name}>{name}</option>)}
          </select>
        </label>
        <span className="spend-filters__range">{rangeLabel}</span>
      </div>
      {isError && (
        <div className="error-banner" role="alert">
          Couldn’t load GitHub activity data. It may be unavailable or you may not be authorised — try refreshing.
        </div>
      )}
      <div className="comparison-summary" aria-label="GitHub period comparison">
        <ComparisonMetric label="Pull requests" selected={visiblePrs.length} comparison={comparisonPrs.length} />
        <ComparisonMetric label="Commits" selected={commits} comparison={previousCommits} />
        <ComparisonMetric label="CI success" selected={ciRate} comparison={previousCiRate} suffix="%" deltaSuffix=" pp" />
        <span className="comparison-summary__basis">vs {comparisonLabel.toLowerCase()}</span>
      </div>
      <div className="panel">
        <div className="panel-title">Pull requests</div>
        <GitHubPrTable prs={visiblePrs} isError={prsError} isLoading={prsLoading} />
      </div>
      <div className="main-grid github-summary-grid">
        <div className="panel">
          <div className="panel-title">Commits by repo</div>
          <GitHubCommitTable summary={visibleSummary} isError={summaryError} isLoading={summaryLoading} />
        </div>
        <div className="panel">
          <div className="panel-title">CI health</div>
          <GitHubCiTable ci={visibleCi} isError={ciError} isLoading={ciLoading} />
        </div>
      </div>
    </div>
  )
}

function successRate(ci: { totalRuns: number; failedRuns: number }[]) {
  const runs = ci.reduce((total, item) => total + item.totalRuns, 0)
  const failures = ci.reduce((total, item) => total + item.failedRuns, 0)
  return runs === 0 ? 0 : Math.round((runs - failures) / runs * 100)
}

function ComparisonMetric({
  label, selected, comparison, suffix = '', deltaSuffix = '',
}: {
  label: string
  selected: number
  comparison: number
  suffix?: string
  deltaSuffix?: string
}) {
  const delta = selected - comparison
  const deltaLabel = delta > 0 ? `+${delta}` : delta < 0 ? `−${Math.abs(delta)}` : 'No change'
  return (
    <div className="comparison-summary__metric" role="group" aria-label={`${label} comparison`}>
      <span className="comparison-summary__label">{label}</span>
      <strong>{selected}{suffix}</strong>
      <span>vs {comparison}{suffix}</span>
      <b>{deltaLabel}{delta === 0 ? '' : deltaSuffix}</b>
    </div>
  )
}
