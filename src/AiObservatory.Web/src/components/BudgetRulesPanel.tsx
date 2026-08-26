import { useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { Button } from '../design/Button'
import { createBudgetRule, deleteBudgetRule } from '../api/client'
import { useBudgetRules, useInsights, useEmailStatus } from '../api/queries'
import { isReadonly } from '../auth/msal'
import { gbp } from '../lib/currency'

const PROVIDERS = ['anthropic', 'copilot', 'google', 'openai'] as const
const PERIODS = ['daily', 'weekly', 'monthly'] as const

const capitalize = (s: string) => s.charAt(0).toUpperCase() + s.slice(1)

function WebhookChip({ configured }: { configured: boolean | undefined }) {
  if (configured === undefined) return null
  return (
    <span
      className="budget-rules__channel"
      style={{
        color: configured ? 'var(--ok-border)' : 'var(--text-muted)',
        border: `1px solid ${configured ? 'var(--ok-border)' : 'var(--border)'}`,
      }}
    >
      Email: {configured ? 'configured' : 'not configured'}
    </span>
  )
}

export default function BudgetRulesPanel() {
  const qc = useQueryClient()
  const { rules, isLoading, isError } = useBudgetRules()
  const insights = useInsights()
  const { configured } = useEmailStatus()

  const [panelOpen, setPanelOpen] = useState(false)
  const [provider, setProvider] = useState<string>('')
  const [period, setPeriod] = useState<'daily' | 'weekly' | 'monthly'>('monthly')
  const [threshold, setThreshold] = useState<string>('')
  const [mutationError, setMutationError] = useState<string | null>(null)
  const [confirmDeleteId, setConfirmDeleteId] = useState<string | null>(null)

  const deleteRule = useMutation({
    mutationFn: deleteBudgetRule,
    onMutate: () => setMutationError(null),
    onSuccess: () => { qc.invalidateQueries({ queryKey: ['budget-rules'] }); setConfirmDeleteId(null) },
    onError: (e: Error) => setMutationError(`Couldn’t remove the rule: ${e.message}`),
  })

  const addRule = useMutation({
    mutationFn: () =>
      createBudgetRule({
        provider: provider === '' ? null : provider,
        period,
        thresholdGbp: parseFloat(threshold),
      }),
    onMutate: () => setMutationError(null),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['budget-rules'] })
      setPanelOpen(false)
      setProvider('')
      setPeriod('monthly')
      setThreshold('')
    },
    onError: (e: Error) => setMutationError(`Couldn’t add the rule: ${e.message}`),
  })

  // Match by title, not insightType: budget-alert insights are typed BudgetAlert now but
  // older rows are Anomaly, and the title prefix is the stable marker across both.
  const budgetAlerts = insights
    .filter(i => i.title.startsWith('Budget alert:'))
    .sort((a, b) => b.generatedAt.localeCompare(a.generatedAt))
    .slice(0, 10)

  function handleOpenPanel() {
    setProvider('')
    setPeriod('monthly')
    setThreshold('')
    setPanelOpen(true)
  }

  function handleSubmit(e: React.FormEvent) {
    e.preventDefault()
    const val = parseFloat(threshold)
    if (!Number.isFinite(val) || val <= 0) return
    addRule.mutate()
  }

  return (
    <section>
      <div className="panel">
        <div className="budget-rules__header">
          <div className="budget-rules__title-row">
            <span className="panel-title">
              Budget Rules
            </span>
            <WebhookChip configured={configured} />
          </div>
          {!isReadonly && (
            <Button variant="ghost" size="sm" onClick={handleOpenPanel} disabled={panelOpen}>
              + Add rule
            </Button>
          )}
        </div>

        <div className="budget-rules__body">
          {isError && <p className="panel-empty">Failed to load budget rules.</p>}
          {mutationError && <p className="panel-empty" role="alert">{mutationError}</p>}
          {!isError && !isLoading && rules.length === 0 && (
            <p className="panel-empty">No budget rules configured.</p>
          )}
          {rules.length > 0 && (
            <table className="budget-rules__table">
              <thead>
                <tr>
                  <th>
                    Provider
                  </th>
                  <th>
                    Period
                  </th>
                  <th>
                    Threshold (GBP)
                  </th>
                  <th>
                    Last fired
                  </th>
                  {!isReadonly && <th aria-label="Actions" />}
                </tr>
              </thead>
              <tbody>
                {rules.map(rule => (
                  <tr key={rule.id}>
                    <td>
                      {rule.provider ? capitalize(rule.provider) : 'All providers'}
                    </td>
                    <td>
                      {capitalize(rule.period)}
                    </td>
                    <td>
                      {gbp(rule.thresholdGbp)}
                    </td>
                    <td>
                      {rule.lastTriggeredAt
                        ? new Date(rule.lastTriggeredAt).toLocaleString()
                        : 'Never'}
                    </td>
                    {!isReadonly && (
                      <td className="budget-rules__actions">
                        {confirmDeleteId === rule.id ? (
                          <span>
                            <Button
                              variant="ghost"
                              size="sm"
                              onClick={() => deleteRule.mutate(rule.id)}
                              disabled={deleteRule.isPending}
                            >
                              Confirm
                            </Button>
                            <Button variant="ghost" size="sm" onClick={() => setConfirmDeleteId(null)}>
                              Cancel
                            </Button>
                          </span>
                        ) : (
                          <Button variant="ghost" size="sm" onClick={() => setConfirmDeleteId(rule.id)}>
                            Remove
                          </Button>
                        )}
                      </td>
                    )}
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </div>
      </div>

      {panelOpen && (
        <div className="panel budget-rules__history">
          <div className="panel-title">Add Budget Rule</div>
          <form onSubmit={handleSubmit}>
            <div className="budget-rules__form-grid">
              <label className="budget-rules__field">
                Provider
                <select
                  value={provider}
                  onChange={e => setProvider(e.target.value)}
                  className="budget-rules__control"
                >
                  <option value="">All providers</option>
                  {PROVIDERS.map(p => (
                    <option key={p} value={p}>{capitalize(p)}</option>
                  ))}
                </select>
              </label>

              <label className="budget-rules__field">
                Period
                <select
                  value={period}
                  onChange={e => setPeriod(e.target.value as typeof period)}
                  className="budget-rules__control"
                >
                  {PERIODS.map(p => (
                    <option key={p} value={p}>{capitalize(p)}</option>
                  ))}
                </select>
              </label>

              <label className="budget-rules__field">
                Threshold (GBP)
                <input
                  type="number"
                  min="0.01"
                  step="0.01"
                  value={threshold}
                  onChange={e => setThreshold(e.target.value)}
                  placeholder="e.g. 50"
                  required
                  className="budget-rules__control"
                />
              </label>
            </div>

            <div className="budget-rules__actions">
              <Button type="submit" variant="primary" size="sm" disabled={addRule.isPending || threshold === ''}>
                {addRule.isPending ? 'Adding...' : 'Add rule'}
              </Button>
              <Button type="button" variant="ghost" size="sm" onClick={() => setPanelOpen(false)}>
                Cancel
              </Button>
            </div>
          </form>
        </div>
      )}

      <div className="panel budget-rules__history">
        <div className="panel-title">Alert History</div>
        {budgetAlerts.length === 0 ? (
          <p className="panel-empty">No budget alerts triggered.</p>
        ) : (
          <div className="budget-rules__history">
            {budgetAlerts.map(alert => (
              <div
                key={alert.id}
                className="insight insight-anomaly"
              >
                <div className="insight-title">{alert.title}</div>
                <div className="insight-body">
                  {alert.body}
                </div>
                <div className="budget-rules__history-time">
                  {new Date(alert.generatedAt).toLocaleString()}
                </div>
              </div>
            ))}
          </div>
        )}
      </div>
    </section>
  )
}
