import { useState, useMemo, lazy, Suspense } from 'react'
import type { ValueType } from 'recharts/types/component/DefaultTooltipContent'
import { useAggregates } from '../api/queries'
import { providerColor } from '../theme/providerColors'
import { useUsdToGbp, gbp } from '../lib/currency'
import { formatShortDate } from '../lib/format'

const TEXT_MUTED = 'var(--text-muted)'

type ChartMode = 'spend' | 'volume' | 'share'

// Inner is defined at module scope so its component identity is stable across
// re-renders. Defining it inside the lazy() factory gave it a new reference on
// every render, causing the chart to remount whenever mode/date state changed.
const ChartInner = lazy(() =>
  import('recharts').then(({ BarChart, Bar, XAxis, YAxis, Tooltip, Legend, ResponsiveContainer }) => ({
    default: function Inner({ byDate, providers, mode }: { byDate: Record<string, string | number>[], providers: string[], mode: ChartMode }) {
      const yTick = (v: number) => {
        if (mode === 'spend') return `£${v}`
        if (mode === 'share') return `${v}%`
        return `${v}M`
      }
      return (
        <ResponsiveContainer width="100%" height={160}>
          <BarChart data={byDate}>
            {/* date is the full ISO yyyy-MM-dd (sorts correctly); shown as "29 May". */}
            <XAxis dataKey="date" tickFormatter={formatShortDate} tick={{ fontSize: 10, fill: TEXT_MUTED }} />
            <YAxis
              tick={{ fontSize: 10, fill: TEXT_MUTED }}
              domain={mode === 'share' ? [0, 100] : undefined}
              tickFormatter={yTick}
            />
            <Tooltip
              contentStyle={{ background: 'var(--card-bg)', border: '1px solid var(--border)', borderRadius: 'var(--r-chip)', color: 'var(--text)' }}
              labelStyle={{ color: 'var(--text)' }}
              itemStyle={{ color: TEXT_MUTED }}
              labelFormatter={label => formatShortDate(String(label ?? ''))}
              formatter={(v: ValueType | undefined) => {
                const num = Number(Array.isArray(v) ? v[0] : v ?? 0)
                if (mode === 'spend') return gbp(num, 3)
                if (mode === 'share') return `${num.toFixed(1)}%`
                return `${num.toFixed(3)}M tokens`
              }}
            />
            <Legend />
            {providers.map((p: string) => (
              <Bar key={p} dataKey={p} stackId="a" fill={providerColor(p)} />
            ))}
          </BarChart>
        </ResponsiveContainer>
      )
    },
  }))
)

interface Props {
  from?: Date
  to?: Date
}

export default function SpendChart({ from, to }: Props) {
  const aggregates = useAggregates(from, to)
  const rate = useUsdToGbp()
  const [mode, setMode] = useState<ChartMode>('spend')

  const byDate = useMemo(() => {
    // Spend and Share both start from GBP spend; Volume from token millions.
    const perDay = aggregates.reduce<Record<string, Record<string, number>>>((acc, a) => {
      acc[a.date] = acc[a.date] ?? {}
      const val = mode === 'volume'
        ? ((a.inputTokens ?? 0) + (a.outputTokens ?? 0)) / 1_000_000
        : a.costUsd * rate
      acc[a.date][a.provider] = (acc[a.date][a.provider] ?? 0) + val
      return acc
    }, {})

    // localeCompare on ISO yyyy-MM-dd == chronological order.
    return Object.entries(perDay)
      .sort((a, b) => a[0].localeCompare(b[0]))
      .map(([date, providers]) => {
        const cleaned: Record<string, number> = {}
        if (mode === 'share') {
          // Normalise each day to 100% so minor providers stay visible despite
          // Anthropic's absolute dominance.
          const total = Object.values(providers).reduce((sum, v) => sum + v, 0)
          for (const [p, val] of Object.entries(providers)) {
            cleaned[p] = total > 0 ? Number(((val / total) * 100).toFixed(2)) : 0
          }
        } else {
          for (const [p, val] of Object.entries(providers)) {
            cleaned[p] = Number(val.toFixed(4))
          }
        }
        return { date, ...cleaned }
      })
  }, [aggregates, rate, mode])

  const providers = useMemo(
    () => Array.from(new Set(aggregates.map(a => a.provider))).toSorted(),
    [aggregates],
  )

  if (byDate.length === 0) return <p className="panel-empty">No spend data for this period.</p>

  const toggleClass = (m: ChartMode) => `chart-toggle-btn ${mode === m ? 'chart-toggle-btn--active' : ''}`

  return (
    <>
      <div className="chart-controls">
        <div className="chart-toggle">
          <button type="button" onClick={() => setMode('spend')} className={toggleClass('spend')}>
            Spend
          </button>
          <button type="button" onClick={() => setMode('volume')} className={toggleClass('volume')}>
            Tokens
          </button>
          <button
            type="button"
            onClick={() => setMode('share')}
            className={toggleClass('share')}
            title="Each day normalised to 100% — shows provider mix when one provider dominates absolute spend"
          >
            Share %
          </button>
        </div>
      </div>
      <Suspense fallback={<div style={{ height: 160 }} className="panel-empty">Loading chart...</div>}>
        <ChartInner byDate={byDate} providers={providers} mode={mode} />
      </Suspense>
    </>
  )
}
