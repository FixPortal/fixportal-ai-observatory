import { gbp } from '../lib/currency'

interface Props {
  total: number
  entryCount: number
  largestCategory: string | null
}

/**
 * Region 2. Every figure here is of the CURRENT filter, not the calendar month —
 * that is what makes "the filtered aggregate" unambiguous.
 */
export default function SpendTotals({ total, entryCount, largestCategory }: Props) {
  return (
    <div className="spend-totals">
      <div className="spend-totals__card">
        <span className="spend-totals__label">Filtered total</span>
        <span className="spend-totals__value">{gbp(total)}</span>
      </div>
      <div className="spend-totals__card">
        <span className="spend-totals__label">Entries</span>
        <span className="spend-totals__value">{entryCount}</span>
      </div>
      <div className="spend-totals__card">
        <span className="spend-totals__label">Largest category</span>
        <span className="spend-totals__value">{largestCategory ?? '—'}</span>
      </div>
    </div>
  )
}
