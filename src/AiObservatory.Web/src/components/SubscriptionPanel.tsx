import { useState, useMemo, useCallback, useRef } from 'react'
import { InfoPopover } from './InfoPopover'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { patchExtraUsage, type Subscription } from '../api/client'
import { useSubscriptions, useAggregates, localDate } from '../api/queries'
import { providerColor } from '../theme/providerColors'
import { gbp, useUsdToGbp, formatCurrency } from '../lib/currency'
import { currentBillingPeriodStart } from '../lib/subscriptions'
import SubscriptionModal from './SubscriptionModal'
import { isReadonly } from '../auth/msal'

const PROVIDER_ORDER: Record<string, number> = { anthropic: 0, copilot: 1, google: 2 }
const capitalize = (s: string) => s.charAt(0).toUpperCase() + s.slice(1)

function ordinal(n: number): string {
  const s = ['th', 'st', 'nd', 'rd']
  const v = n % 100
  return n + (s[(v - 20) % 10] ?? s[v] ?? s[0])
}

function ExtraUsageChip({ sub }: { sub: Subscription }) {
  const qc = useQueryClient()
  const [editing, setEditing] = useState(false)
  const [draft, setDraft] = useState('')
  const escapedRef = useRef(false)
  const focusInput = useCallback((el: HTMLInputElement | null) => { el?.focus() }, [])

  const patch = useMutation({
    mutationFn: (amount: number | null) => patchExtraUsage(sub.id, amount),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['subscriptions'] })
      setEditing(false)
    },
    onError: (err: Error) => {
      alert(`Failed to save extra usage cost: ${err.message}`)
    },
  })

  // Read-only viewers (share link) can't patch — show the value, no edit affordance.
  if (isReadonly) {
    return (
      <span className={`sub-card__extra-chip${sub.extraUsageCost === null ? ' sub-card__extra-chip--null' : ''}`}>
        {sub.extraUsageCost !== null ? `+ ${formatCurrency(sub.extraUsageCost, sub.currency)}` : '—'}
      </span>
    )
  }

  if (editing) {
    return (
      <input
        ref={focusInput}
        className="sub-card__extra-input"
        type="number"
        step="0.01"
        min="0"
        aria-label="Extra usage cost"
        value={draft}
        onChange={e => setDraft(e.target.value)}
        onBlur={() => {
          if (escapedRef.current) { escapedRef.current = false; return }
          const trimmed = draft.trim()
          if (trimmed === '') { patch.mutate(null); return } // blank clears the value
          const val = parseFloat(trimmed)
          // Reject negatives (min="0" only constrains the spinner, not typed input): a
          // negative would deflate the total, % of budget, and over-budget flag.
          if (!Number.isFinite(val) || val < 0) { setEditing(false); return }
          patch.mutate(val)
        }}
        onKeyDown={e => {
          if (e.key === 'Enter') (e.target as HTMLInputElement).blur()
          if (e.key === 'Escape') {
            escapedRef.current = true
            setDraft('')
            setEditing(false)
          }
        }}
      />
    )
  }

  return (
    <button
      type="button"
      className={`sub-card__extra-chip${sub.extraUsageCost === null ? ' sub-card__extra-chip--null' : ''}`}
      onClick={() => {
        escapedRef.current = false
        setDraft(sub.extraUsageCost !== null ? String(sub.extraUsageCost) : '')
        setEditing(true)
      }}
      title="Click to edit extra usage"
    >
      {sub.extraUsageCost !== null ? `+ ${formatCurrency(sub.extraUsageCost, sub.currency)}` : '— add'}
    </button>
  )
}

interface GroupedSubscription {
  provider: string
  name: string
  costAmount: number
  extraUsageCost: number | null
  currency: string
  billingDay: number
  activeFrom: string
  activeTo: string | null
  originalSubscription: Subscription // reference for patching extra usage
  memberCount: number // subs merged into this card; >1 disables inline extra-usage edit
}

export default function SubscriptionPanel() {
  const { subscriptions, isError: subscriptionsError, isLoading: subscriptionsLoading } = useSubscriptions()
  const aggregates = useAggregates()
  const rate = useUsdToGbp()
  const [modalOpen, setModalOpen] = useState(false)
  const today = localDate(new Date())

  const active = useMemo(
    () =>
      subscriptions
        .filter(s => s.activeFrom <= today && (s.activeTo === null || s.activeTo >= today))
        .sort((a, b) => {
          const ak = a.provider.toLowerCase()
          const bk = b.provider.toLowerCase()
          return (PROVIDER_ORDER[ak] ?? 99) - (PROVIDER_ORDER[bk] ?? 99)
        }),
    [subscriptions, today]
  )

  // Collapse subscriptions into one card ONLY when they share provider AND currency AND
  // billing day. Merging across different currencies summed raw amounts into a nonsense
  // total and % denominator; merging different billing days measured the second sub against
  // the wrong period window and mislabelled the renewal day. Differing subs stay separate.
  const collapsed = useMemo(() => {
    const groups: Record<string, GroupedSubscription> = {}
    for (const sub of active) {
      const key = `${sub.provider.toLowerCase()}|${sub.currency.toUpperCase()}|${sub.billingDay}`
      if (!groups[key]) {
        groups[key] = {
          provider: sub.provider,
          name: sub.name,
          costAmount: 0,
          extraUsageCost: null,
          currency: sub.currency,
          billingDay: sub.billingDay,
          activeFrom: sub.activeFrom,
          activeTo: sub.activeTo,
          originalSubscription: sub,
          memberCount: 0,
        }
      }
      const g = groups[key]
      g.costAmount += sub.costAmount
      if (sub.extraUsageCost !== null) {
        g.extraUsageCost = (g.extraUsageCost ?? 0) + sub.extraUsageCost
      }
      if (g.originalSubscription.id !== sub.id) {
        g.name = `${g.name} + ${sub.name}`
      }
      if (sub.activeFrom < g.activeFrom) {
        g.activeFrom = sub.activeFrom
      }
      g.memberCount += 1
    }
    return Object.values(groups)
  }, [active])

  return (
    <div className="sub-panel">
      <div className="panel">
        <div className="sub-panel-header">
          <div className="sub-panel-title-row">
            <span className="sub-panel-title">Subscriptions</span>
            <InfoPopover id="subscriptions-info" title="Subscriptions">
              <p>Period spend is API-tracked usage from the start of each provider's current billing cycle to today.</p>
              <p>The cycle resets on the renewal day shown in each card. The progress bar shows period spend as a percentage of the monthly cost.</p>
            </InfoPopover>
          </div>
          {!isReadonly && (
            <button type="button" className="sub-panel-btn" onClick={() => setModalOpen(true)}>
              Manage subscriptions
            </button>
          )}
        </div>

        {subscriptionsError ? (
          <p className="panel-empty">Failed to load subscriptions.</p>
        ) : subscriptionsLoading ? null : collapsed.length === 0 ? (
          <div className="sub-empty">
            <p className="sub-empty__text">No subscriptions — add one to start tracking.</p>
            {!isReadonly && (
              <button type="button" className="sub-panel-btn" onClick={() => setModalOpen(true)}>
                Add subscription
              </button>
            )}
          </div>
        ) : (
          <div className="sub-cards">
            {collapsed.map(sub => {
              const providerKey = sub.provider.toLowerCase()
              const start = currentBillingPeriodStart(sub.billingDay, today)
              const windowStart = sub.activeFrom > start ? sub.activeFrom : start
              
              const periodSpendUsd = aggregates
                .filter(a => a.provider === providerKey && a.date >= windowStart)
                .reduce((acc, a) => acc + a.costUsd, 0)
              const periodSpendGbp = periodSpendUsd * rate
              
              const total = sub.costAmount + (sub.extraUsageCost ?? 0)
              const totalGbp = sub.currency.toUpperCase() === 'USD' ? total * rate : total
              
              const ratio = totalGbp > 0 ? periodSpendGbp / totalGbp : 0
              const pct = Math.min(ratio * 100, 100)
              const isOver = ratio > 1
              const accentColor = providerColor(providerKey)

              return (
                <div key={providerKey} className="sub-card" style={{ borderLeftColor: accentColor }}>
                  <div className="sub-card__header">
                    <span className="sub-card__provider" style={{ color: accentColor }}>
                      {capitalize(sub.provider)}
                    </span>
                    <span className="sub-card__plan">{sub.name}</span>
                  </div>

                  <div className="sub-card__cost">
                    {formatCurrency(sub.costAmount, sub.currency)}
                    <span className="sub-card__cost-unit"> /mo</span>
                  </div>

                  <div className="sub-card__extra">
                    <span className="sub-card__extra-label">Extra:</span>
                    {sub.memberCount > 1 ? (
                      // Multiple plans merged: show the combined extra read-only — inline
                      // editing here would only patch the first plan (edit each individually
                      // via Manage subscriptions).
                      <span
                        className={`sub-card__extra-chip${sub.extraUsageCost === null ? ' sub-card__extra-chip--null' : ''}`}
                        title="Combined extra usage across merged plans — edit each plan via Manage subscriptions"
                      >
                        {sub.extraUsageCost !== null ? `+ ${formatCurrency(sub.extraUsageCost, sub.currency)}` : '—'}
                      </span>
                    ) : (
                      <ExtraUsageChip sub={sub.originalSubscription} />
                    )}
                  </div>

                  <div className="sub-card__billing-day">
                    Renews on the {ordinal(sub.billingDay)}
                  </div>

                  <div className="sub-card__period-row">
                    <span className="sub-card__period-label">Period spend</span>
                    <span className={`sub-card__period-value${isOver ? ' sub-card__period-value--over' : ''}`}>
                      {gbp(periodSpendGbp)}
                    </span>
                  </div>

                  <div className="sub-progress-track">
                    <div
                      className={`sub-progress-fill${isOver ? ' sub-progress-fill--over' : ''}`}
                      style={{ width: `${pct}%`, background: isOver ? undefined : accentColor }}
                    />
                  </div>
                  <div className="sub-progress-label">
                    {Math.round(ratio * 100)}% of {formatCurrency(total, sub.currency)} total
                  </div>
                </div>
              )
            })}
          </div>
        )}
      </div>

      {modalOpen && <SubscriptionModal open={modalOpen} onClose={() => setModalOpen(false)} />}
    </div>
  )
}
