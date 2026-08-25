import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, expect, test, vi } from 'vitest'
import type { DailyAggregate } from '../api/client'
import ProviderSplit, { buildProviderSlices } from './ProviderSplit'

const data = vi.hoisted(() => ({ aggregates: [] as DailyAggregate[] }))
vi.mock('../api/queries', () => ({ useAggregates: () => data.aggregates }))

const aggregate = (overrides: Partial<DailyAggregate> = {}): DailyAggregate => ({
  date: '2026-08-24', provider: 'openai', model: 'gpt-5', sourceId: 'openai-usage-api',
  sourceKind: 'providerApi', usageScope: 'api', costBasis: 'listPriceEstimate', inputTokens: 100,
  outputTokens: 50, cacheReadTokens: 0, cacheWriteTokens: 0, cacheWrite1hTokens: 0,
  costUsd: 999, unknownCostCount: 0, cacheSavingsUsd: 0, unknownCacheSavingsCount: 0,
  requestCount: 2, ...overrides,
})

beforeEach(() => { data.aggregates = [] })

test('builds token and activity shares without reading cost', () => {
  const rows = [aggregate({ provider: 'openai', costUsd: 999 }), aggregate({ provider: 'anthropic', inputTokens: 50, outputTokens: 0, requestCount: 6, costUsd: 0 })]
  expect(buildProviderSlices(rows, 'tokens')).toEqual([
    { provider: 'anthropic', name: 'Anthropic', value: 50, share: 25 },
    { provider: 'openai', name: 'OpenAI', value: 150, share: 75 },
  ])
  expect(buildProviderSlices(rows, 'activity').map(slice => [slice.name, slice.value])).toEqual([
    ['Anthropic', 6], ['OpenAI', 2],
  ])
})

test('keeps arbitrary provider labels readable', () => {
  expect(buildProviderSlices([aggregate({ provider: 'new-oss-provider' })], 'tokens')[0].name).toBe('New oss provider')
})

test('uses native accessible toggles and reports zero selected totals truthfully', async () => {
  data.aggregates = [aggregate({ inputTokens: 100, outputTokens: 0, requestCount: 0 })]
  render(<ProviderSplit />)
  expect(screen.getByRole('button', { name: 'Tokens' })).toHaveAttribute('aria-pressed', 'true')
  const activity = screen.getByRole('button', { name: 'Activity' })
  activity.focus()
  expect(activity).toHaveFocus()
  await userEvent.keyboard('{Enter}')
  expect(screen.getByText('Not reported for this period.')).toBeInTheDocument()
})
