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

  if (!stats || stats.sessions === 0) return null

  const totalTokens = stats.totalOutputTokens + stats.totalEstSavedTokens
  const compressionPct = totalTokens > 0
    ? Math.round((stats.totalEstSavedTokens / totalTokens) * 100)
    : 0

  return (
    <div className="summary-cards" style={{ marginTop: 0 }}>
      <Card>
        <div className="card-label">Caveman sessions</div>
        <div className="card-value">{stats.sessions.toLocaleString()}</div>
      </Card>
      <Card>
        <div className="card-label">Tokens saved (est.)</div>
        <div className="card-value">{fmtTokens(stats.totalEstSavedTokens)}</div>
        <div className="card-sub">of {fmtTokens(totalTokens)} total output</div>
      </Card>
      <Card>
        <div className="card-label">Compression rate (est.)</div>
        <div className="card-value">{compressionPct}%</div>
        <div className="card-sub">full mode benchmark</div>
      </Card>
      <Card>
        <div className="card-label">Est. value saved</div>
        <div className="card-value card-value--lead">{formatGbp(stats.totalEstSavedUsd, rate)}</div>
      </Card>
    </div>
  )
}
