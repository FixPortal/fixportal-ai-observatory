import { lazy, Suspense, useMemo } from 'react'
import type { ValueType } from 'recharts/types/component/DefaultTooltipContent'
import { useActivityDaily } from '../api/queries'
import { formatActiveTime } from '../lib/duration'
import { formatShortDate } from '../lib/format'

const TEXT_MUTED = 'var(--text-muted)'

const ChartInner = lazy(() =>
  import('recharts').then(({ BarChart, Bar, XAxis, YAxis, Tooltip, ResponsiveContainer }) => ({
    default: function Inner({ byDate }: { byDate: { date: string; minutes: number }[] }) {
      return (
        <ResponsiveContainer width="100%" height={160}>
          <BarChart data={byDate}>
            <XAxis dataKey="date" tickFormatter={formatShortDate} tick={{ fontSize: 10, fill: TEXT_MUTED }} />
            <YAxis tick={{ fontSize: 10, fill: TEXT_MUTED }} tickFormatter={(v: number) => `${v}m`} />
            <Tooltip
              contentStyle={{ background: 'var(--card-bg)', border: '1px solid var(--border)', borderRadius: 'var(--r-chip)', color: 'var(--text)' }}
              labelStyle={{ color: 'var(--text)' }}
              itemStyle={{ color: TEXT_MUTED }}
              labelFormatter={(label) => formatShortDate(String(label ?? ''))}
              formatter={(v: ValueType | undefined) => formatActiveTime(Number(Array.isArray(v) ? v[0] : v ?? 0) * 60)}
            />
            <Bar dataKey="minutes" fill="var(--brand)" />
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

export default function ActivityTrendChart({ from, to }: Props) {
  const { daily, isError } = useActivityDaily(from, to)

  const byDate = useMemo(
    () => daily
      .toSorted((a, b) => a.date.localeCompare(b.date))
      .map((d) => ({ date: d.date, minutes: Math.round(d.activeSeconds / 60) })),
    [daily],
  )

  if (isError) return <p className="panel-empty">Couldn’t load activity — try refreshing.</p>
  if (byDate.length === 0) return <p className="panel-empty">No activity data for this period.</p>

  return (
    <Suspense fallback={<div style={{ height: 160 }} className="panel-empty">Loading chart...</div>}>
      <ChartInner byDate={byDate} />
    </Suspense>
  )
}
