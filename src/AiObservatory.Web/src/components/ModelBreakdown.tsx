import { useState, useMemo } from 'react'
import type { KeyboardEvent } from 'react'
import { useAggregates } from '../api/queries'
import { useUsdToGbp, formatGbp } from '../lib/currency'
import { providerColor } from '../theme/providerColors'
import { getProvider, PROVIDER_ORDER } from '../config/providers'

type SortField = 'model' | 'requests' | 'cost' | 'cpm'
type SortDirection = 'asc' | 'desc'

const formatProviderName = (p: string) =>
  getProvider(p)?.displayName ?? (p.charAt(0).toUpperCase() + p.slice(1))

const SearchIcon = () => (
  <svg
    className="model-breakdown-search-icon"
    xmlns="http://www.w3.org/2000/svg"
    viewBox="0 0 24 24"
    fill="none"
    stroke="currentColor"
    strokeWidth="2.5"
    strokeLinecap="round"
    strokeLinejoin="round"
    aria-hidden="true"
  >
    <circle cx="11" cy="11" r="8" />
    <path d="m21 21-4.3-4.3" />
  </svg>
)

interface SortableHeaderProps {
  field: SortField
  label: string
  hint?: string
  sortField: SortField
  sortDirection: SortDirection
  onSort: (field: SortField) => void
}

const SortableHeader = ({
  field,
  label,
  hint,
  sortField,
  sortDirection,
  onSort,
}: SortableHeaderProps) => {
  const isActive = sortField === field
  const handleKeyDown = (e: KeyboardEvent) => {
    if (e.key === 'Enter' || e.key === ' ') {
      e.preventDefault()
      onSort(field)
    }
  }

  // Determine aria-sort value
  let ariaSort: 'ascending' | 'descending' | 'none' = 'none'
  if (isActive) {
    ariaSort = sortDirection === 'asc' ? 'ascending' : 'descending'
  }

  // Determine sort indicator character
  let indicatorSymbol = '↕'
  if (isActive) {
    indicatorSymbol = sortDirection === 'asc' ? '▲' : '▼'
  }

  return (
    <th
      onClick={() => onSort(field)}
      onKeyDown={handleKeyDown}
      className="sortable-header"
      style={{ cursor: 'pointer' }}
      tabIndex={0}
      aria-sort={ariaSort}
      title={hint}
    >
      <span className="sortable-header__content">
        {label}
        <span className={`sort-indicator ${isActive ? 'sort-indicator--active' : ''}`} aria-hidden="true">
          {indicatorSymbol}
        </span>
      </span>
    </th>
  )
}

export default function ModelBreakdown() {
  const aggregates = useAggregates()
  const rate = useUsdToGbp()

  // State for sorting and filtering
  const [searchQuery, setSearchQuery] = useState('')
  const [selectedProvider, setSelectedProvider] = useState('all')
  const [sortField, setSortField] = useState<SortField>('cost')
  const [sortDirection, setSortDirection] = useState<SortDirection>('desc')

  // Keyed by provider + model: the same model name can arrive from two
  // providers (e.g. claude-sonnet served by Anthropic and by Copilot) and
  // those are distinct series, not duplicates.
  const rawByModel = useMemo(
    () => Object.entries(
      aggregates.reduce<Record<string, { provider: string; model: string; cost: number; requests: number; inputTokens: number; outputTokens: number }>>((acc, a) => {
        const key = `${a.provider}|${a.model}`
        acc[key] = acc[key] ?? { provider: a.provider, model: a.model, cost: 0, requests: 0, inputTokens: 0, outputTokens: 0 }
        acc[key].cost += a.costUsd
        acc[key].requests += a.requestCount
        acc[key].inputTokens += (a.inputTokens ?? 0)
        acc[key].outputTokens += (a.outputTokens ?? 0)
        return acc
      }, {})
    ).map(([key, value]) => {
      const totalTokens = value.inputTokens + value.outputTokens
      const cpm = totalTokens > 0 ? (value.cost / totalTokens) * 1_000_000 : 0
      return { key, ...value, cpm }
    }),
    [aggregates],
  )

  // Extract unique providers for filter buttons
  const uniqueProviders = useMemo(() => {
    const providers = new Set(rawByModel.map((item) => item.provider))
    return Array.from(providers).sort((a, b) => {
      const idxA = PROVIDER_ORDER.indexOf(a)
      const idxB = PROVIDER_ORDER.indexOf(b)
      if (idxA !== -1 && idxB !== -1) return idxA - idxB
      if (idxA !== -1) return -1
      if (idxB !== -1) return 1
      return a.localeCompare(b)
    })
  }, [rawByModel])

  // Filter models
  const filteredByModel = useMemo(() => {
    return rawByModel.filter((item) => {
      const matchesProvider = selectedProvider === 'all' || item.provider === selectedProvider
      const matchesSearch = item.model.toLowerCase().includes(searchQuery.trim().toLowerCase())
      return matchesProvider && matchesSearch
    })
  }, [rawByModel, selectedProvider, searchQuery])

  // Sort models
  const sortedAndFilteredByModel = useMemo(() => {
    return filteredByModel.toSorted((a, b) => {
      let comparison: number
      if (sortField === 'model') {
        comparison = a.model.localeCompare(b.model)
      } else if (sortField === 'requests') {
        comparison = a.requests - b.requests
      } else if (sortField === 'cpm') {
        comparison = a.cpm - b.cpm
      } else {
        comparison = a.cost - b.cost
      }
      return sortDirection === 'asc' ? comparison : -comparison
    })
  }, [filteredByModel, sortField, sortDirection])

  // Use maximum cost of unfiltered data to maintain a consistent visual scale for the progress bars
  const maxCost = useMemo(() => Math.max(...rawByModel.map((item) => item.cost), 1), [rawByModel])

  if (rawByModel.length === 0) return <p className="panel-empty">No usage data for this period.</p>

  const handleSort = (field: SortField) => {
    if (sortField === field) {
      setSortDirection((prev) => (prev === 'asc' ? 'desc' : 'asc'))
    } else {
      setSortField(field)
      setSortDirection('desc') // default to desc for numeric, can change for model
    }
  }

  return (
    <>
      <div className="model-breakdown-controls">
        <div className="model-breakdown-search-container">
          <SearchIcon />
          <input
            type="text"
            placeholder="Search models..."
            value={searchQuery}
            onChange={(e) => setSearchQuery(e.target.value)}
            className="model-breakdown-search"
            aria-label="Search models"
          />
        </div>
        <div className="model-breakdown-filters">
          <button
            type="button"
            onClick={() => setSelectedProvider('all')}
            className="filter-chip"
            style={
              selectedProvider === 'all'
                ? {
                    borderColor: 'var(--brand)',
                    color: 'var(--text)',
                    fontWeight: 600,
                    background: 'var(--app-bg)',
                  }
                : undefined
            }
          >
            All
          </button>
          {uniqueProviders.map((p) => (
            <button
              key={p}
              type="button"
              onClick={() => setSelectedProvider(p)}
              className="filter-chip"
              style={
                selectedProvider === p
                  ? {
                      borderColor: providerColor(p),
                      color: 'var(--text)',
                      fontWeight: 600,
                      background: 'var(--app-bg)',
                    }
                  : undefined
              }
            >
              <span className="filter-chip__dot" style={{ background: providerColor(p) }} />
              {formatProviderName(p)}
            </button>
          ))}
        </div>
      </div>

      {sortedAndFilteredByModel.length === 0 ? (
        <p className="panel-empty">No matching models found.</p>
      ) : (
        <table className="model-table">
          <thead>
            <tr>
              <SortableHeader
                field="model"
                label="Model"
                sortField={sortField}
                sortDirection={sortDirection}
                onSort={handleSort}
              />
              <SortableHeader
                field="requests"
                label="Requests"
                sortField={sortField}
                sortDirection={sortDirection}
                onSort={handleSort}
              />
              <SortableHeader
                field="cost"
                label="Cost"
                sortField={sortField}
                sortDirection={sortDirection}
                onSort={handleSort}
              />
              <SortableHeader
                field="cpm"
                label="Cost / 1M"
                hint="Cost per 1 million tokens (input + output) — the per-row second figure"
                sortField={sortField}
                sortDirection={sortDirection}
                onSort={handleSort}
              />
            </tr>
          </thead>
          <tbody>
            {sortedAndFilteredByModel.map(({ key, provider, model, cost, requests, cpm }) => (
              <tr key={key}>
                <td>
                  <span
                    className="model-table__dot"
                    style={{ background: providerColor(provider) }}
                    title={provider}
                  />
                  {model}
                </td>
                <td>{requests.toLocaleString()}</td>
                <td>
                  {formatGbp(cost, rate)}
                  <div className="bar-mini" style={{ width: `${(cost / maxCost) * 100}%` }} />
                </td>
                <td>{cpm === 0 ? '—' : formatGbp(cpm, rate)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </>
  )
}
