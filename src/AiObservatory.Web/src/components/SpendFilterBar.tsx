import type { SpendCategory, SpendVendor } from '../api/client'

interface Props {
  categories: SpendCategory[]
  vendors: SpendVendor[]
  categoryId?: string
  vendorId?: string
  onCategoryChange: (id: string | undefined) => void
  onVendorChange: (id: string | undefined) => void
  onAddEntry: () => void
  onManageCatalog: () => void
  canEdit: boolean
}

/** Region 1. One filter state, lifted to SpendPage, drives every other region. */
export default function SpendFilterBar({
  categories, vendors, categoryId, vendorId,
  onCategoryChange, onVendorChange, onAddEntry, onManageCatalog, canEdit,
}: Props) {
  return (
    <div className="spend-filters">
      <label className="spend-filters__field">
        <span>Category</span>
        <select
          value={categoryId ?? ''}
          onChange={e => onCategoryChange(e.target.value || undefined)}
        >
          <option value="">All categories</option>
          {categories.map(c => <option key={c.id} value={c.id}>{c.displayName}</option>)}
        </select>
      </label>

      <label className="spend-filters__field">
        <span>Vendor</span>
        <select
          value={vendorId ?? ''}
          onChange={e => onVendorChange(e.target.value || undefined)}
        >
          <option value="">All vendors</option>
          {vendors.map(v => <option key={v.id} value={v.id}>{v.displayName}</option>)}
        </select>
      </label>

      {canEdit && (
        <>
          <button type="button" className="spend-filters__add" onClick={onAddEntry}>
            Add entry
          </button>
          <button type="button" className="spend-filters__add" onClick={onManageCatalog}>
            Manage catalog
          </button>
        </>
      )}
    </div>
  )
}
