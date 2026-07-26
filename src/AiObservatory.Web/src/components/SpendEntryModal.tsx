import { useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { postSpendEntries, type NewSpendEntry, type SpendCategory, type SpendVendor } from '../api/client'
import { localDate } from '../api/queries'

interface Props {
  categories: SpendCategory[]
  vendors: SpendVendor[]
  /** The page's visible date range. Bounds the date input so a charge dated outside
   * it cannot be saved and then simply not appear in the (currently unfiltered-by-date)
   * ledger table -- there is no date picker to widen the view until phase 2. */
  from: Date
  to: Date
  onClose: () => void
}

export default function SpendEntryModal({ categories, vendors, from, to, onClose }: Props) {
  const minDate = localDate(from)
  const maxDate = localDate(to)
  const qc = useQueryClient()
  const [occurredOn, setOccurredOn] = useState(() => localDate(new Date()))
  const [vendorId, setVendorId] = useState(vendors[0]?.id ?? '')
  const [categoryId, setCategoryId] = useState(vendors[0]?.defaultCategoryId ?? categories[0]?.id ?? '')
  const [amount, setAmount] = useState('')
  const [currency, setCurrency] = useState('GBP')
  const [description, setDescription] = useState('')
  const [formError, setFormError] = useState<string | null>(null)
  const [verdictError, setVerdictError] = useState<string | null>(null)

  const save = useMutation({
    mutationFn: (entry: NewSpendEntry) => postSpendEntries([entry]),
    onSuccess: results => {
      const result = results[0]
      // A per-row verdict is not an HTTP failure, so it has to be read rather than assumed.
      if (result?.status === 'rejected') {
        setVerdictError(result.reason ?? 'Entry rejected')
        return
      }
      qc.invalidateQueries({ queryKey: ['spend-entries'] })
      onClose()
    },
    onError: (err: Error) => setVerdictError(err.message),
  })

  function onVendorChange(id: string) {
    setVendorId(id)
    // Follow the vendor's default category, but only as a starting point.
    const preferred = vendors.find(v => v.id === id)?.defaultCategoryId
    if (preferred) setCategoryId(preferred)
  }

  function handleSave() {
    setFormError(null)
    setVerdictError(null)
    const parsed = Number(amount)
    if (amount.trim() === '' || !Number.isFinite(parsed) || parsed < 0) {
      setFormError('Amount must be a non-negative number')
      return
    }
    if (!vendorId || !categoryId) {
      setFormError('Pick a vendor and a category')
      return
    }
    // min/max on the input steer the native picker, but noValidate (above) means the
    // form will still submit a typed-in out-of-range date. Without this check, a charge
    // dated outside the visible window would save, return `created`, and then simply not
    // appear -- there is no date picker to widen the view and find it until phase 2.
    if (occurredOn < minDate || occurredOn > maxDate) {
      setFormError(`Date must be between ${minDate} and ${maxDate}`)
      return
    }

    save.mutate({
      occurredOn,
      vendorId,
      categoryId,
      amount: parsed,
      currency,
      description: description.trim() || null,
      source: 'manual',
      // Manual rows are deliberately un-keyed: a person entering the same charge twice
      // should see two rows and notice, not have the second silently swallowed by
      // deduplication meant for re-imported files.
      entryKey: null,
    })
  }

  const error = formError ?? verdictError

  return (
    <dialog
      ref={el => { if (el && !el.open) el.showModal() }}
      className="modal"
      aria-labelledby="spend-entry-modal-title"
      onClose={onClose}
    >
      <div className="modal__header">
        <span id="spend-entry-modal-title" className="modal__title">Add spend entry</span>
        <button type="button" className="modal__close" onClick={onClose} aria-label="Close">×</button>
      </div>

      <div className="modal__body">
        <div className="sub-form">
          {/* noValidate: this form shows its own role="alert" message instead of
              relying on native constraint-validation bubbles (which would also
              silently swallow the submit event before handleSave ever runs). */}
          <form noValidate onSubmit={e => { e.preventDefault(); handleSave() }}>
            <div className="sub-form__grid">
              <div>
                <label htmlFor="spend-entry-date" className="sub-form__label">Date</label>
                <input
                  id="spend-entry-date"
                  className="sub-form__input"
                  type="date"
                  min={minDate}
                  max={maxDate}
                  value={occurredOn}
                  onChange={e => setOccurredOn(e.target.value)}
                />
              </div>
              <div>
                <label htmlFor="spend-entry-vendor" className="sub-form__label">Vendor</label>
                <select
                  id="spend-entry-vendor"
                  className="sub-form__select"
                  value={vendorId}
                  onChange={e => onVendorChange(e.target.value)}
                >
                  {vendors.map(v => <option key={v.id} value={v.id}>{v.displayName}</option>)}
                </select>
              </div>
              <div>
                <label htmlFor="spend-entry-category" className="sub-form__label">Category</label>
                <select
                  id="spend-entry-category"
                  className="sub-form__select"
                  value={categoryId}
                  onChange={e => setCategoryId(e.target.value)}
                >
                  {categories.map(c => <option key={c.id} value={c.id}>{c.displayName}</option>)}
                </select>
              </div>
              <div>
                <label htmlFor="spend-entry-amount" className="sub-form__label">Amount</label>
                <input
                  id="spend-entry-amount"
                  className="sub-form__input"
                  type="number"
                  step="0.01"
                  min="0"
                  value={amount}
                  onChange={e => setAmount(e.target.value)}
                  placeholder="0.00"
                />
              </div>
              <div>
                <label htmlFor="spend-entry-currency" className="sub-form__label">Currency</label>
                <select
                  id="spend-entry-currency"
                  className="sub-form__select"
                  value={currency}
                  onChange={e => setCurrency(e.target.value)}
                >
                  <option value="GBP">GBP (£)</option>
                  <option value="USD">USD ($)</option>
                </select>
              </div>
              <div>
                <label htmlFor="spend-entry-description" className="sub-form__label">Description (optional)</label>
                <input
                  id="spend-entry-description"
                  className="sub-form__input"
                  type="text"
                  maxLength={200}
                  value={description}
                  onChange={e => setDescription(e.target.value)}
                />
              </div>
            </div>

            {error && <p className="modal__error" role="alert">{error}</p>}

            <div className="sub-form__actions">
              <button
                type="button"
                className="sub-form__btn sub-form__btn--secondary"
                onClick={onClose}
                disabled={save.isPending}
              >
                Cancel
              </button>
              <button
                type="submit"
                className="sub-form__btn sub-form__btn--primary"
                disabled={save.isPending}
              >
                {save.isPending ? 'Saving...' : 'Save'}
              </button>
            </div>
          </form>
        </div>
      </div>
    </dialog>
  )
}
