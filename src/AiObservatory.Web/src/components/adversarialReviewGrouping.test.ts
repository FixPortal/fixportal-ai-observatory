import { test, expect } from 'vitest'
import { groupRuns, formatDuration } from './adversarialReviewGrouping'
import type { AdversarialReviewRun } from '../api/client'

function run(p: Partial<AdversarialReviewRun>): AdversarialReviewRun {
  return {
    id: Math.random().toString(36).slice(2),
    reviewer: 'anthropic', model: 'claude-sonnet-4-6', role: 'reviewer', repo: 'r',
    inputTokens: 0, outputTokens: 0, costUsd: 0, reviewDurationMs: 0,
    issuesRaised: 0, issuesAccepted: 0, costPerAcceptedFinding: null,
    runId: 'R1', recordedAt: '2026-06-27T12:00:00Z', ...p,
  }
}

test('groups participants by runId', () => {
  const groups = groupRuns([
    run({ runId: 'R1', reviewer: 'anthropic', role: 'reviewer' }),
    run({ runId: 'R1', reviewer: 'google', role: 'reviewer' }),
    run({ runId: 'R1', reviewer: 'openai', role: 'reviewer' }),
    run({ runId: 'R1', reviewer: 'anthropic', role: 'judge', model: 'claude-opus-4-8' }),
  ])
  expect(groups).toHaveLength(1)
  expect(groups[0].participants).toHaveLength(4)
})

test('sums per-column totals across participants', () => {
  const groups = groupRuns([
    run({ runId: 'R1', reviewer: 'anthropic', issuesRaised: 4, issuesAccepted: 3, costUsd: 0.2, reviewDurationMs: 1000 }),
    run({ runId: 'R1', reviewer: 'openai', issuesRaised: 5, issuesAccepted: 4, costUsd: 0.4, reviewDurationMs: 2000 }),
  ])
  expect(groups[0].totals).toEqual({ raised: 9, accepted: 7, costUsd: 0.6, durationMs: 3000 })
})

test('flags incomplete when fewer than 3 reviewer vendors', () => {
  const groups = groupRuns([run({ runId: 'R1', reviewer: 'anthropic', role: 'reviewer' })])
  expect(groups[0].isComplete).toBe(false)
  expect(groups[0].statusReason).toContain('1 of 3')
})

test('complete needs 3 reviewer vendors and a judge', () => {
  const base = ['anthropic', 'google', 'openai'].map(v => run({ runId: 'R1', reviewer: v, role: 'reviewer' }))
  expect(groupRuns(base)[0].isComplete).toBe(false) // no judge yet
  expect(groupRuns([...base, run({ runId: 'R1', reviewer: 'anthropic', role: 'judge' })])[0].isComplete).toBe(true)
})

test('orders participants reviewers-then-judge, newest run first', () => {
  const groups = groupRuns([
    run({ runId: 'OLD', recordedAt: '2026-06-20T00:00:00Z' }),
    run({ runId: 'NEW', recordedAt: '2026-06-27T00:00:00Z', reviewer: 'anthropic', role: 'judge' }),
    run({ runId: 'NEW', recordedAt: '2026-06-27T00:00:00Z', reviewer: 'openai', role: 'reviewer' }),
  ])
  expect(groups[0].runId).toBe('NEW')
  expect(groups[0].participants.map(p => p.role)).toEqual(['reviewer', 'judge'])
})

test('group recordedAt is the earliest participant time, independent of sort order', () => {
  // Judge recorded last but sorts last; an unknown vendor recorded first but
  // would sort oddly. The group timestamp must be the earliest, not participants[0].
  const groups = groupRuns([
    run({ runId: 'R1', reviewer: 'openai', role: 'reviewer', recordedAt: '2026-06-27T12:00:30Z' }),
    run({ runId: 'R1', reviewer: 'anthropic', role: 'judge', recordedAt: '2026-06-27T12:00:45Z' }),
    run({ runId: 'R1', reviewer: 'anthropic', role: 'reviewer', recordedAt: '2026-06-27T12:00:05Z' }),
  ])
  expect(groups[0].recordedAt).toBe('2026-06-27T12:00:05Z')
})

test('unknown vendor sorts after known reviewers and before the judge', () => {
  const groups = groupRuns([
    run({ runId: 'R1', reviewer: 'anthropic', role: 'judge' }),
    run({ runId: 'R1', reviewer: 'mistral', role: 'reviewer' }),
    run({ runId: 'R1', reviewer: 'anthropic', role: 'reviewer' }),
    run({ runId: 'R1', reviewer: 'openai', role: 'reviewer' }),
  ])
  expect(groups[0].participants.map(p => p.reviewer)).toEqual(['anthropic', 'openai', 'mistral', 'anthropic'])
  expect(groups[0].participants.map(p => p.role)).toEqual(['reviewer', 'reviewer', 'reviewer', 'judge'])
})

test('formatDuration renders seconds and minutes', () => {
  expect(formatDuration(38000)).toBe('38s')
  expect(formatDuration(64000)).toBe('1m04s')
  expect(formatDuration(0)).toBe('0s')
})
