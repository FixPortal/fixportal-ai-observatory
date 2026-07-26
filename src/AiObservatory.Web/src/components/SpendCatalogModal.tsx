import { useState } from 'react'
import type { SpendCategory, SpendVendor } from '../api/client'
import SpendCategoryCatalog from './SpendCategoryCatalog'
import SpendVendorCatalog from './SpendVendorCatalog'

interface Props {
  /** Both incl. archived rows -- SpendPage already loads these via
   * useAllSpendCategories/useAllSpendVendors, so the modal reuses them rather than
   * re-fetching. */
  categories: SpendCategory[]
  vendors: SpendVendor[]
  onClose: () => void
}

type Tab = 'categories' | 'vendors'

/** Task 9: the spend catalog panel. Both axes (categories, vendors) live in one
 * modal behind a tab switch rather than two stacked sections -- each axis already
 * has its own list-plus-create-form, and stacking both would make the dialog a
 * long scroll for whichever axis the admin isn't touching right now. No new
 * dashboard tab, no routing -- opened from SpendFilterBar, same pattern as
 * SpendEntryModal. */
export default function SpendCatalogModal({ categories, vendors, onClose }: Props) {
  const [tab, setTab] = useState<Tab>('categories')

  return (
    <dialog
      ref={el => { if (el && !el.open) el.showModal() }}
      className="modal"
      aria-labelledby="spend-catalog-modal-title"
      onClose={onClose}
    >
      <div className="modal__header">
        <span id="spend-catalog-modal-title" className="modal__title">Manage spend catalog</span>
        <button type="button" className="modal__close" onClick={onClose} aria-label="Close">×</button>
      </div>

      <div className="modal__body">
        <div className="catalog-tabs" role="tablist" aria-label="Catalog axis">
          <button
            type="button"
            role="tab"
            aria-selected={tab === 'categories'}
            className="catalog-tab"
            onClick={() => setTab('categories')}
          >
            Categories
          </button>
          <button
            type="button"
            role="tab"
            aria-selected={tab === 'vendors'}
            className="catalog-tab"
            onClick={() => setTab('vendors')}
          >
            Vendors
          </button>
        </div>

        {tab === 'categories'
          ? <SpendCategoryCatalog categories={categories} />
          : <SpendVendorCatalog vendors={vendors} categories={categories} />}
      </div>
    </dialog>
  )
}
