// Self-check for the local usage sweeper's pure logic. Zero deps:
//   node --test clients/observatory-sweep.test.mjs
import { test } from 'node:test'
import assert from 'node:assert/strict'
import {
  pickRates, costUsd, parseCodex, parseCopilot, parseClaude, parseKimi,
  buildDailySnapshots, updateFileCache, parseLocalSources, codexSidFromPath,
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

test('parseClaude keeps one copy of each assistant message and preserves pricing dimensions', () => {
  const content = [
    JSON.stringify({ type: 'assistant', timestamp: '2026-08-24T12:00:00Z', message: { id: 'msg-1', model: 'claude-opus-5', usage: { input_tokens: 2, output_tokens: 10, cache_read_input_tokens: 20, cache_creation_input_tokens: 30, thinking_tokens: 4, cache_creation: { ephemeral_5m_input_tokens: 5, ephemeral_1h_input_tokens: 25 }, service_tier: 'standard', speed: 'standard', inference_geo: 'not_available' } } }),
    JSON.stringify({ type: 'assistant', timestamp: '2026-08-24T12:00:01Z', message: { id: 'msg-1', model: 'claude-opus-5', usage: { input_tokens: 2, output_tokens: 10, cache_read_input_tokens: 20, cache_creation_input_tokens: 30 } } }),
  ].join('\n')

  const records = parseClaude(content)

  assert.equal(records.length, 1)
  assert.deepEqual(records[0], {
    tool: 'claude', messageId: 'msg-1', date: '2026-08-24', model: 'claude-opus-5',
    occurredAtUtc: '2026-08-24T12:00:00.000Z', inputTokens: 2, outputTokens: 10,
    cacheReadTokens: 20, cacheWriteTokens: 30, cacheWrite1hTokens: 25,
    cacheWrite5mTokens: 5, thoughtTokens: 4, serviceTier: 'standard', speed: 'standard',
    inferenceGeo: 'not_available',
  })
})

test('buildDailySnapshots deduplicates Claude message ids across transcript files', () => {
  const line = JSON.stringify({ type: 'assistant', timestamp: '2026-08-24T12:00:00Z', message: { id: 'msg-copy', model: 'claude-opus-5', usage: { input_tokens: 2, output_tokens: 10 } } })

  const snapshots = buildDailySnapshots([...parseClaude(line), ...parseClaude(line)])

  assert.equal(snapshots.length, 1)
  assert.equal(snapshots[0].inputTokens, 2)
})

test('buildDailySnapshots keeps the richest Claude copy when duplicate transcripts differ', () => {
  const sparse = JSON.stringify({ type: 'assistant', timestamp: '2026-08-24T12:00:01Z', message: { id: 'msg-richest', model: 'claude-opus-5', usage: { input_tokens: 2, output_tokens: 10, cache_creation_input_tokens: 30 } } })
  const full = JSON.stringify({ type: 'assistant', timestamp: '2026-08-24T12:00:00Z', message: { id: 'msg-richest', model: 'claude-opus-5', usage: { input_tokens: 2, output_tokens: 10, cache_creation_input_tokens: 30, cache_creation: { ephemeral_5m_input_tokens: 5, ephemeral_1h_input_tokens: 25 }, service_tier: 'standard', speed: 'fast', inference_geo: 'us' } } })

  const snapshots = buildDailySnapshots([...parseClaude(sparse), ...parseClaude(full)])

  assert.equal(snapshots.length, 1)
  assert.equal(snapshots[0].eventKey, 'claude:2026-08-24:claude-opus-5:standard:fast:us')
  assert.equal(snapshots[0].cacheWrite1hTokens, 25)
})

test('parseKimi reads usage.record and ignores its mirrored step.end usage', () => {
  const usage = { inputOther: 10, output: 2, inputCacheRead: 20, inputCacheCreation: 3 }
  const content = [
    JSON.stringify({ type: 'usage.record', time: 1787572800000, model: 'kimi-code/kimi-for-coding', usage }),
    JSON.stringify({ type: 'context.append_loop_event', time: 1787572800001, event: { type: 'step.end', usage } }),
  ].join('\n')

  const records = parseKimi(content)

  assert.equal(records.length, 1)
  assert.equal(records[0].inputTokens, 10)
  assert.equal(records[0].outputTokens, 2)
  assert.equal(records[0].cacheReadTokens, 20)
  assert.equal(records[0].cacheWriteTokens, 3)
  assert.equal(records[0].occurredAtUtc, '2026-08-24T12:00:00.000Z')
})

test('local parsers ignore records without a valid observation timestamp', () => {
  const claude = parseClaude(JSON.stringify({ type: 'assistant', timestamp: null, message: { id: 'msg-no-time', model: 'claude-opus-5', usage: { input_tokens: 1, output_tokens: 1 } } }))
  const kimi = parseKimi(JSON.stringify({ type: 'usage.record', time: null, model: 'kimi-code/kimi-for-coding', usage: { inputOther: 1, output: 1 } }))

  assert.deepEqual(claude, [])
  assert.deepEqual(kimi, [])
})

test('buildDailySnapshots sums sessions into one stable cumulative day/model key', () => {
  const records = [
    { tool: 'codex', sessionId: 'a', date: '2026-08-24', model: 'gpt-5.4', occurredAtUtc: '2026-08-24T10:00:00Z', cum: { input: 10, output: 2, cacheRead: 1, cacheWrite: 0 } },
    { tool: 'codex', sessionId: 'b', date: '2026-08-24', model: 'gpt-5.4', occurredAtUtc: '2026-08-24T12:00:00Z', cum: { input: 20, output: 3, cacheRead: 2, cacheWrite: 0 } },
  ]

  const snapshots = buildDailySnapshots(records)

  assert.equal(snapshots.length, 1)
  assert.equal(snapshots[0].eventKey, 'codex:2026-08-24:gpt-5.4')
  assert.equal(snapshots[0].inputTokens, 30)
  assert.equal(snapshots[0].outputTokens, 5)
  assert.equal(snapshots[0].cacheReadTokens, 3)
  assert.equal(snapshots[0].occurredAtUtc, '2026-08-24T12:00:00.000Z')
  assert.equal(snapshots[0].sourceId, 'codex-local')
  assert.equal(snapshots[0].usageScope, 'subscription')
  assert.equal(snapshots[0].costBasis, 'notional')
})

test('buildDailySnapshots replaces a changed transcript under the same key', () => {
  const record = input => ({ tool: 'codex', sessionId: 'a', date: '2026-08-24', model: 'gpt-5.4', cum: { input, output: 2, cacheRead: 1, cacheWrite: 0 } })

  const before = buildDailySnapshots([record(10)])[0]
  const after = buildDailySnapshots([record(25)])[0]

  assert.equal(after.eventKey, before.eventKey)
  assert.equal(after.inputTokens, 25)
})

test('buildDailySnapshots keeps Claude grouping dimensions and Kimi cost unknown', () => {
  const claude = parseClaude(JSON.stringify({ type: 'assistant', timestamp: '2026-08-24T12:00:00Z', message: { id: 'msg-dimensions', model: 'claude-opus-5', usage: { input_tokens: 2, output_tokens: 10, cache_creation_input_tokens: 30, thinking_tokens: 4, cache_creation: { ephemeral_5m_input_tokens: 5, ephemeral_1h_input_tokens: 25 }, service_tier: 'standard', speed: 'fast', inference_geo: 'us' } } }))
  const kimi = parseKimi(JSON.stringify({ type: 'usage.record', time: 1787572800000, model: 'kimi-code/kimi-for-coding', usage: { inputOther: 10, output: 2, inputCacheRead: 20, inputCacheCreation: 3 } }))

  const snapshots = buildDailySnapshots([...claude, ...kimi])
  const claudeSnapshot = snapshots.find(x => x.sourceId === 'claude-local')
  const kimiSnapshot = snapshots.find(x => x.sourceId === 'kimi-local')

  assert.equal(claudeSnapshot.eventKey, 'claude:2026-08-24:claude-opus-5:standard:fast:us')
  assert.equal(claudeSnapshot.cacheWrite1hTokens, 25)
  assert.equal(claudeSnapshot.thoughtTokens, 4)
  assert.equal(kimiSnapshot.eventKey, 'kimi:2026-08-24:kimi-code/kimi-for-coding')
  assert.equal(kimiSnapshot.costUsd, null)
})

test('updateFileCache parses only changed paths and drops files outside the active scan', async () => {
  const cache = {
    same: { mtimeMs: 1, records: [{ sessionId: 'same' }] },
    removed: { mtimeMs: 1, records: [{ sessionId: 'removed' }] },
  }
  const reads = []
  const files = [{ path: 'same', mtimeMs: 1 }, { path: 'changed', mtimeMs: 2 }]

  const result = await updateFileCache(
    files,
    cache,
    content => [{ sessionId: content }],
    async path => { reads.push(path); return path },
  )

  assert.deepEqual(reads, ['changed'])
  assert.deepEqual(result.records.map(x => x.sessionId).sort(), ['changed', 'same'])
  assert.deepEqual(Object.keys(result.cache).sort(), ['changed', 'same'])
})

test('parseLocalSources defaults to every collector and honors an explicit allowlist', () => {
  assert.deepEqual([...parseLocalSources()].sort(), ['claude', 'codex', 'copilot', 'kimi'])
  assert.deepEqual([...parseLocalSources('codex,kimi')].sort(), ['codex', 'kimi'])
})

test('codexSidFromPath extracts the trailing UUID', () => {
  const p = '/x/sessions/2026/05/28/rollout-2026-05-28T09-02-11-019e6d9a-f12f-7f02-ac67-61b284977a18.jsonl'
  assert.equal(codexSidFromPath(p), '019e6d9a-f12f-7f02-ac67-61b284977a18')
})
