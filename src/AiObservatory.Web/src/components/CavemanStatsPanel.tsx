import { CollapsiblePanel } from './CollapsiblePanel'
import { Card } from '../design/Card'
import { useCavemanStats } from '../api/queries'
import { useUsdToGbp, formatGbp } from '../lib/currency'

function fmtTokens(n: number): string {
  if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(1)}M`
  if (n >= 1_000) return `${(n / 1_000).toFixed(0)}k`
  return String(n)
}

export default function CavemanStatsPanel() {
  const stats = useCavemanStats()
  const rate = useUsdToGbp()

  const summaryLine = stats && stats.sessions > 0
    ? `${stats.sessions.toLocaleString()} sessions · ${formatGbp(stats.totalEstSavedUsd, rate)} saved`
    : 'No sessions yet'

  const totalTokens = (stats?.totalOutputTokens ?? 0) + (stats?.totalEstSavedTokens ?? 0)
  const compressionPct = totalTokens > 0
    ? Math.round(((stats?.totalEstSavedTokens ?? 0) / totalTokens) * 100)
    : 0

  return (
    <CollapsiblePanel id="caveman" title="Caveman" summary={summaryLine}>
      <div className="summary-cards">
        <Card>
          <div className="card-label">Caveman sessions</div>
          <div className="card-value">{stats?.sessions.toLocaleString() ?? '—'}</div>
        </Card>
        <Card>
          <div className="card-label">Tokens saved (est.)</div>
          <div className="card-value">{stats ? fmtTokens(stats.totalEstSavedTokens) : '—'}</div>
          <div className="card-sub">of {stats ? fmtTokens(totalTokens) : '—'} total output</div>
        </Card>
        <Card>
          <div className="card-label">Compression rate (est.)</div>
          <div className="card-value">{stats ? `${compressionPct}%` : '—'}</div>
          <div className="card-sub">full mode benchmark</div>
        </Card>
        <Card>
          <div className="card-label">Est. value saved</div>
          <div className="card-value card-value--lead">
            {stats ? formatGbp(stats.totalEstSavedUsd, rate) : '—'}
          </div>
        </Card>
      </div>
    </CollapsiblePanel>
  )
}
