import { useMemo, lazy, Suspense } from 'react'
import type { ValueType } from 'recharts/types/component/DefaultTooltipContent'
import { useAggregates } from '../api/queries'
import { providerColor } from '../theme/providerColors'
import { useUsdToGbp, gbp } from '../lib/currency'

const ChartInner = lazy(async () => {
  const { PieChart, Pie, Cell, Tooltip, Legend, ResponsiveContainer } = await import('recharts')
  
  interface ProviderSlice { name: string; value: number }
  return {
    default: function Inner({ data }: { data: ProviderSlice[] }) {
      return (
        <ResponsiveContainer width="100%" height={200}>
          <PieChart>
            <Pie data={data} dataKey="value" nameKey="name" innerRadius={50} outerRadius={80}>
              {data.map((entry) => (
                <Cell key={entry.name} fill={providerColor(entry.name)} />
              ))}
            </Pie>
            <Tooltip
              contentStyle={{ background: 'var(--card-bg)', border: '1px solid var(--border)', borderRadius: 'var(--r-chip)', color: 'var(--text)' }}
              labelStyle={{ color: 'var(--text)' }}
              itemStyle={{ color: 'var(--text-muted)' }}
              formatter={(v: ValueType | undefined) => gbp(Number(Array.isArray(v) ? v[0] : v ?? 0))}
            />
            <Legend />
          </PieChart>
        </ResponsiveContainer>
      )
    }
  }
})

interface Props {
  from?: Date
  to?: Date
}

export default function ProviderSplit({ from, to }: Props) {
  const aggregates = useAggregates(from, to)
  const rate = useUsdToGbp()

  const data = useMemo(
    () => Object.entries(
      aggregates.reduce<Record<string, number>>((acc, a) => {
        acc[a.provider] = (acc[a.provider] ?? 0) + a.costUsd
        return acc
      }, {})
    ).map(([name, value]) => ({ name, value: +(value * rate).toFixed(4) })),
    [aggregates, rate],
  )

  if (data.length === 0) return <p className="panel-empty">No spend data for this period.</p>

  return (
    <Suspense fallback={<div style={{ height: 200 }} className="panel-empty">Loading chart...</div>}>
      <ChartInner data={data} />
    </Suspense>
  )
}
