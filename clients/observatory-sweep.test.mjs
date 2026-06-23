// Self-check for the local usage sweeper's pure logic. Zero deps:
//   node --test clients/observatory-sweep.test.mjs
import { test } from 'node:test'
import assert from 'node:assert/strict'
import {
  pickRates, costUsd, deltaTokens, parseCodex, parseCopilot, codexSidFromPath,
} from './observatory-sweep.mjs'

test('pickRates resolves the longest matching prefix, not the first', () => {
  const table = { 'gpt-4o': [1, 1, 1, 1], 'gpt-4o-mini': [9, 9, 9, 9] }
  assert.deepEqual(pickRates(table, 'gpt-4o-mini-2024-07-18', [0, 0, 0, 0]), [9, 9, 9, 9])
  assert.deepEqual(pickRates(table, 'gpt-4o-2024', [0, 0, 0, 0]), [1, 1, 1, 1])
  assert.deepEqual(pickRates(table, 'unknown', [0, 0, 0, 0]), [0, 0, 0, 0])
})

test('costUsd applies each rate to its own token bucket', () => {
  // 1M input @2, 1M output @8, 1M cacheRead @0.5, 0 write => 2 + 8 + 0.5 = 10.5
  const c = costUsd([2, 8, 0.5, 0], { input: 1e6, output: 1e6, cacheRead: 1e6, cacheWrite: 0 })
  assert.equal(c, 10.5)
})

test('deltaTokens subtracts prior cumulative', () => {
  const d = deltaTokens(
    { input: 100, output: 50, cacheRead: 10, cacheWrite: 0 },
    { input: 175, output: 70, cacheRead: 10, cacheWrite: 0 },
  )
  assert.deepEqual(d, { input: 75, output: 20, cacheRead: 0, cacheWrite: 0 })
})

test('deltaTokens falls back to current totals when cumulative goes backwards', () => {
  // log rotated / session reset: prev > cum -> emit cum, never a negative
  const d = deltaTokens(
    { input: 1000, output: 1000, cacheRead: 0, cacheWrite: 0 },
    { input: 30, output: 20, cacheRead: 5, cacheWrite: 0 },
  )
  assert.deepEqual(d, { input: 30, output: 20, cacheRead: 5, cacheWrite: 0 })
})

test('deltaTokens treats missing prev as zero', () => {
  const cum = { input: 10, output: 5, cacheRead: 2, cacheWrite: 1 }
  assert.deepEqual(deltaTokens(undefined, cum), cum)
})

test('parseCodex takes the last token_count and splits cached input out', () => {
  const lines = [
    JSON.stringify({ type: 'turn_context', payload: { model: 'gpt-5.5' } }),
    JSON.stringify({ timestamp: '2026-06-01T10:00:00Z', type: 'event_msg', payload: { type: 'token_count', info: { total_token_usage: { input_tokens: 100, cached_input_tokens: 40, output_tokens: 25, reasoning_output_tokens: 7, total_tokens: 125 } } } }),
    // a later, larger cumulative reading wins
    JSON.stringify({ timestamp: '2026-06-01T10:05:00Z', type: 'event_msg', payload: { type: 'token_count', info: { total_token_usage: { input_tokens: 300, cached_input_tokens: 100, output_tokens: 90, reasoning_output_tokens: 12, total_tokens: 390 } } } }),
  ].join('\n')
  const r = parseCodex(lines)
  assert.equal(r.model, 'gpt-5.5')
  assert.equal(r.endedAt, '2026-06-01T10:05:00Z')
  assert.equal(r.reasoning, 12)
  // input billable = 300 - 100 cached; cacheRead = 100; output whole (incl reasoning)
  assert.deepEqual(r.cum, { input: 200, output: 90, cacheRead: 100, cacheWrite: 0 })
})

test('parseCodex returns null when no token_count is present', () => {
  const lines = JSON.stringify({ type: 'turn_context', payload: { model: 'gpt-5' } })
  assert.equal(parseCodex(lines), null)
})

test('parseCodex defaults the model when no turn_context carries one', () => {
  const line = JSON.stringify({ type: 'event_msg', payload: { type: 'token_count', info: { total_token_usage: { input_tokens: 10, cached_input_tokens: 0, output_tokens: 5 } } } })
  assert.equal(parseCodex(line).model, 'gpt-5')
})

test('parseCopilot reads modelMetrics from the shutdown event and splits cache reads', () => {
  const shutdown = {
    type: 'session.shutdown',
    timestamp: '2026-06-13T18:25:23.144Z',
    data: {
      modelMetrics: {
        'gpt-5.4': { usage: { inputTokens: 185510, outputTokens: 7710, cacheReadTokens: 141312, cacheWriteTokens: 0, reasoningTokens: 6068 } },
      },
    },
  }
  const r = parseCopilot(['{"type":"session.start"}', JSON.stringify(shutdown)].join('\n'))
  assert.equal(r.endedAt, '2026-06-13T18:25:23.144Z')
  // input billable = 185510 - 141312 = 44198
  assert.deepEqual(r.perModel['gpt-5.4'], { input: 44198, output: 7710, cacheRead: 141312, cacheWrite: 0, reasoning: 6068 })
})

test('parseCopilot returns null when the session has not shut down', () => {
  assert.equal(parseCopilot('{"type":"session.start"}\n{"type":"turn"}'), null)
})

test('codexSidFromPath extracts the trailing UUID', () => {
  const p = '/x/sessions/2026/05/28/rollout-2026-05-28T09-02-11-019e6d9a-f12f-7f02-ac67-61b284977a18.jsonl'
  assert.equal(codexSidFromPath(p), '019e6d9a-f12f-7f02-ac67-61b284977a18')
})
