import { Bar, BarChart, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts'
import type { ValueType } from 'recharts/types/component/DefaultTooltipContent'
import { gbp } from '../lib/currency'
import { formatShortDate } from '../lib/format'
import type { BilledReporting } from '../api/client'

const TEXT_MUTED = 'var(--text-muted)'

interface Props {
  data: BilledReporting['dailySeries']
}

export default function BilledSpendChart({ data }: Props) {
  return (
    <ResponsiveContainer width="100%" height={160}>
      <BarChart data={data}>
        <XAxis dataKey="date" tickFormatter={formatShortDate} tick={{ fontSize: 10, fill: TEXT_MUTED }} />
        <YAxis tick={{ fontSize: 10, fill: TEXT_MUTED }} tickFormatter={(value: number) => gbp(value)} />
        <Tooltip
          contentStyle={{ background: 'var(--card-bg)', border: '1px solid var(--border)', borderRadius: 'var(--r-chip)', color: 'var(--text)' }}
          labelStyle={{ color: 'var(--text)' }}
          itemStyle={{ color: TEXT_MUTED }}
          labelFormatter={label => formatShortDate(String(label ?? ''))}
          formatter={(value: ValueType | undefined) => gbp(Number(Array.isArray(value) ? value[0] : value ?? 0))}
        />
        <Bar dataKey="amountGbp" name="Billed spend" fill="var(--brand)" />
      </BarChart>
    </ResponsiveContainer>
  )
}
