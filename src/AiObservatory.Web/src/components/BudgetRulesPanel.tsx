import { useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { Button } from '../design/Button'
import { createBudgetRule, deleteBudgetRule } from '../api/client'
import { useBudgetRules, useInsights, useEmailStatus } from '../api/queries'

const PROVIDERS = ['anthropic', 'copilot', 'google', 'openai'] as const
const PERIODS = ['daily', 'weekly', 'monthly'] as const

const capitalize = (s: string) => s.charAt(0).toUpperCase() + s.slice(1)

function WebhookChip({ configured }: { configured: boolean | undefined }) {
  if (configured === undefined) return null
  return (
    <span
      style={{
        fontSize: '0.75rem',
        fontWeight: 500,
        padding: '2px 8px',
        borderRadius: 4,
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

  const deleteRule = useMutation({
    mutationFn: deleteBudgetRule,
    onSuccess: () => qc.invalidateQueries({ queryKey: ['budget-rules'] }),
  })

  const addRule = useMutation({
    mutationFn: () =>
      createBudgetRule({
        provider: provider === '' ? null : provider,
        period,
        thresholdUsd: parseFloat(threshold),
      }),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['budget-rules'] })
      setPanelOpen(false)
      setProvider('')
      setPeriod('monthly')
      setThreshold('')
    },
  })

  const budgetAlerts = insights
    .filter(i => i.insightType === 'anomaly' && i.title.startsWith('Budget alert:'))
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
        <div
          style={{
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'space-between',
            gap: 'var(--space-3)',
            flexWrap: 'wrap',
          }}
        >
          <div style={{ display: 'flex', alignItems: 'center', gap: 'var(--space-3)', flexWrap: 'wrap' }}>
            <span className="panel-title" style={{ marginBottom: 0, paddingBottom: 0, borderBottom: 'none' }}>
              Budget Rules
            </span>
            <WebhookChip configured={configured} />
          </div>
          <Button variant="ghost" size="sm" onClick={handleOpenPanel} disabled={panelOpen}>
            + Add rule
          </Button>
        </div>

        <div style={{ marginTop: 'var(--space-3)', borderTop: '1px solid var(--border)', paddingTop: 'var(--space-3)' }}>
          {isError && <p className="panel-empty">Failed to load budget rules.</p>}
          {!isError && !isLoading && rules.length === 0 && (
            <p className="panel-empty">No budget rules configured.</p>
          )}
          {rules.length > 0 && (
            <table style={{ width: '100%', borderCollapse: 'collapse', fontSize: '0.82rem' }}>
              <thead>
                <tr style={{ borderBottom: '1px solid var(--border)' }}>
                  <th style={{ textAlign: 'left', padding: '4px 8px 4px 0', color: 'var(--text-muted)', fontWeight: 600 }}>
                    Provider
                  </th>
                  <th style={{ textAlign: 'left', padding: '4px 8px', color: 'var(--text-muted)', fontWeight: 600 }}>
                    Period
                  </th>
                  <th style={{ textAlign: 'right', padding: '4px 8px', color: 'var(--text-muted)', fontWeight: 600 }}>
                    Threshold (USD)
                  </th>
                  <th style={{ textAlign: 'left', padding: '4px 8px', color: 'var(--text-muted)', fontWeight: 600 }}>
                    Last fired
                  </th>
                  <th style={{ width: 32 }} aria-label="Actions" />
                </tr>
              </thead>
              <tbody>
                {rules.map(rule => (
                  <tr key={rule.id} style={{ borderBottom: '1px solid var(--border)' }}>
                    <td style={{ padding: '6px 8px 6px 0', color: 'var(--text)' }}>
                      {rule.provider ? capitalize(rule.provider) : 'All providers'}
                    </td>
                    <td style={{ padding: '6px 8px', color: 'var(--text)' }}>
                      {capitalize(rule.period)}
                    </td>
                    <td style={{ padding: '6px 8px', color: 'var(--text)', textAlign: 'right' }}>
                      ${rule.thresholdUsd.toFixed(2)}
                    </td>
                    <td style={{ padding: '6px 8px', color: 'var(--text-muted)' }}>
                      {rule.lastTriggeredAt
                        ? new Date(rule.lastTriggeredAt).toLocaleString()
                        : 'Never'}
                    </td>
                    <td style={{ padding: '6px 0 6px 8px', textAlign: 'center' }}>
                      <Button
                        variant="ghost"
                        size="sm"
                        onClick={() => deleteRule.mutate(rule.id)}
                        disabled={deleteRule.isPending}
                      >
                        Remove
                      </Button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </div>
      </div>

      {panelOpen && (
        <div className="panel" style={{ marginTop: 'var(--space-3)' }}>
          <div className="panel-title">Add Budget Rule</div>
          <form onSubmit={handleSubmit}>
            <div
              style={{
                display: 'grid',
                gridTemplateColumns: 'repeat(auto-fit, minmax(160px, 1fr))',
                gap: 'var(--space-3)',
                marginBottom: 'var(--space-4)',
              }}
            >
              <label style={{ display: 'flex', flexDirection: 'column', gap: 4, fontSize: '0.8rem', color: 'var(--text-muted)' }}>
                Provider
                <select
                  value={provider}
                  onChange={e => setProvider(e.target.value)}
                  style={{
                    font: 'inherit',
                    fontSize: '0.85rem',
                    padding: '5px 8px',
                    borderRadius: 4,
                    border: '1px solid var(--border)',
                    background: 'var(--card-bg)',
                    color: 'var(--text)',
                  }}
                >
                  <option value="">All providers</option>
                  {PROVIDERS.map(p => (
                    <option key={p} value={p}>{capitalize(p)}</option>
                  ))}
                </select>
              </label>

              <label style={{ display: 'flex', flexDirection: 'column', gap: 4, fontSize: '0.8rem', color: 'var(--text-muted)' }}>
                Period
                <select
                  value={period}
                  onChange={e => setPeriod(e.target.value as typeof period)}
                  style={{
                    font: 'inherit',
                    fontSize: '0.85rem',
                    padding: '5px 8px',
                    borderRadius: 4,
                    border: '1px solid var(--border)',
                    background: 'var(--card-bg)',
                    color: 'var(--text)',
                  }}
                >
                  {PERIODS.map(p => (
                    <option key={p} value={p}>{capitalize(p)}</option>
                  ))}
                </select>
              </label>

              <label style={{ display: 'flex', flexDirection: 'column', gap: 4, fontSize: '0.8rem', color: 'var(--text-muted)' }}>
                Threshold (USD)
                <input
                  type="number"
                  min="0.01"
                  step="0.01"
                  value={threshold}
                  onChange={e => setThreshold(e.target.value)}
                  placeholder="e.g. 50"
                  required
                  style={{
                    font: 'inherit',
                    fontSize: '0.85rem',
                    padding: '5px 8px',
                    borderRadius: 4,
                    border: '1px solid var(--border)',
                    background: 'var(--card-bg)',
                    color: 'var(--text)',
                  }}
                />
              </label>
            </div>

            <div style={{ display: 'flex', gap: 'var(--space-2)' }}>
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

      <div className="panel" style={{ marginTop: 'var(--space-3)' }}>
        <div className="panel-title">Alert History</div>
        {budgetAlerts.length === 0 ? (
          <p className="panel-empty">No budget alerts triggered.</p>
        ) : (
          <div style={{ display: 'flex', flexDirection: 'column', gap: 'var(--space-2)' }}>
            {budgetAlerts.map(alert => (
              <div
                key={alert.id}
                className="insight insight-anomaly"
                style={{ padding: 'var(--space-3)' }}
              >
                <div className="insight-title">{alert.title}</div>
                <div className="insight-body" style={{ marginTop: 'var(--space-1)' }}>
                  {alert.body}
                </div>
                <div style={{ fontSize: '0.75rem', color: 'var(--text-muted)', marginTop: 'var(--space-1)' }}>
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
