import type { SourceStatusResponse } from '../api/client'
import { useSourceStatuses } from '../api/queries'
import { NON_PROVIDER_SOURCES, PROVIDERS, getSource, providerDisplayName, sourceDisplayName, type ProviderSource } from '../config/providers'
import { StatusBadge } from '../design/StatusBadge'

/* eslint-disable react-refresh/only-export-components -- focused tests exercise deterministic merge and time formatting helpers */

export interface SourceStatusRow extends SourceStatusResponse {
  provider?: string
  displayName: string
  setupHref?: string
}

const registrySources: (ProviderSource & { provider?: string })[] = [
  ...PROVIDERS.flatMap(provider => provider.sources.map(source => ({ ...source, provider: provider.key }))),
  ...NON_PROVIDER_SOURCES,
]

const missingStatus = (sourceId: string): SourceStatusResponse => ({
  sourceId,
  status: 'notConfigured',
  isConfigured: false,
  lastAttemptAt: null,
  lastSuccessAt: null,
  latestObservationAt: null,
  consecutiveFailureCount: 0,
  lastError: null,
})

export function mergeSourceStatuses(statuses: SourceStatusResponse[]): SourceStatusRow[] {
  const api = new Map(statuses.map(status => [status.sourceId, status]))
  const known = registrySources.map(source => ({
    ...(api.get(source.id) ?? missingStatus(source.id)),
    provider: source.provider,
    displayName: source.displayName,
    setupHref: source.setupHref,
  }))
  const unknown = statuses
    .filter(status => !getSource(status.sourceId))
    .map(status => ({ ...status, displayName: sourceDisplayName(status.sourceId) }))
    .toSorted((a, b) => a.displayName.localeCompare(b.displayName) || a.sourceId.localeCompare(b.sourceId))
  return [...known, ...unknown]
}

export interface LastSuccessDisplay { relative: string; absolute: string; dateTime: string }

export function formatLastSuccess(value: string | null, now = new Date()): LastSuccessDisplay | null {
  if (!value) return null
  const date = new Date(value)
  if (Number.isNaN(date.getTime())) return null
  const seconds = (date.getTime() - now.getTime()) / 1000
  const [amount, unit]: [number, Intl.RelativeTimeFormatUnit] = Math.abs(seconds) >= 86_400
    ? [Math.round(seconds / 86_400), 'day']
    : Math.abs(seconds) >= 3_600
      ? [Math.round(seconds / 3_600), 'hour']
      : [Math.round(seconds / 60), 'minute']
  const parts = new Intl.DateTimeFormat('en-GB', {
    timeZone: 'Europe/London', day: '2-digit', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit', hour12: false,
  }).formatToParts(date)
  const part = (type: Intl.DateTimeFormatPartTypes) => parts.find(item => item.type === type)?.value ?? ''
  return {
    relative: new Intl.RelativeTimeFormat('en-GB', { numeric: 'always' }).format(amount, unit),
    absolute: `${part('day')} ${part('month')} ${part('year')}, ${part('hour')}:${part('minute')}`,
    dateTime: value,
  }
}

const statusPresentation: Record<string, { variant: 'ok' | 'warn' | 'bad' | 'info'; label: string }> = {
  fresh: { variant: 'ok', label: 'Fresh' },
  configured: { variant: 'info', label: 'Configured' },
  stale: { variant: 'warn', label: 'Stale' },
  failing: { variant: 'bad', label: 'Failing' },
  unavailable: { variant: 'warn', label: 'Unavailable' },
  notConfigured: { variant: 'info', label: 'Not configured' },
}

export default function SourceStatusPanel() {
  const { statuses, isError, isLoading } = useSourceStatuses()
  if (isError) return null
  const rows = mergeSourceStatuses(statuses)

  return (
    <section className="source-status" aria-label="Source freshness" aria-busy={isLoading || undefined}>
      <div className="source-status__header">
        <h2>Source freshness</h2>
        <span>{rows.length} capabilities</span>
      </div>
      <ul className="source-status__list">
        {rows.map(row => {
          const status = isLoading
            ? { variant: 'info' as const, label: 'Loading' }
            : statusPresentation[row.status] ?? { variant: 'info' as const, label: sourceDisplayName(row.status) }
          const lastSuccess = formatLastSuccess(row.lastSuccessAt)
          return (
            <li key={row.sourceId} className="source-status__row">
              <div className="source-status__identity">
                <span className="source-status__name">{row.displayName}</span>
                {row.provider && <span className="source-status__provider">{providerDisplayName(row.provider)}</span>}
              </div>
              <StatusBadge variant={status.variant} label={status.label} />
              <span className="source-status__success">
                {isLoading ? 'Waiting for status' : lastSuccess ? <time dateTime={lastSuccess.dateTime}>{lastSuccess.relative} · {lastSuccess.absolute}</time> : 'Not reported'}
              </span>
              <span className="source-status__failures">
                {isLoading ? '—' : `${row.consecutiveFailureCount.toLocaleString()} ${row.consecutiveFailureCount === 1 ? 'failure' : 'failures'}`}
              </span>
              {!isLoading && row.status === 'notConfigured' && row.setupHref && (
                <a className="source-status__setup" href={row.setupHref} aria-label={`Setup: ${row.displayName}`}>Setup</a>
              )}
              {row.lastError && (
                <details className="source-status__error">
                  <summary aria-label={`Show error for ${row.displayName}`}>Error</summary>
                  <p>{row.lastError}</p>
                </details>
              )}
            </li>
          )
        })}
      </ul>
    </section>
  )
}
