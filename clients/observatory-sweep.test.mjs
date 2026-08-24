// Self-check for the local usage sweeper's pure logic. Zero deps:
//   node --test clients/observatory-sweep.test.mjs
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { mkdtemp, mkdir, rm, utimes, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import {
  pickRates, costUsd, parseCodex, parseCopilot, parseClaude, parseKimi,
  buildDailySnapshots, updateFileCache, parseLocalSources, listJsonl,
  planSnapshotSubmissions, recordSuccessfulSubmission,
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

test('parseCodex ignores valid JSON values that are not telemetry objects', () => {
  const content = ['null', '0', 'true', '"text"', '[]'].join('\n')

  assert.equal(parseCodex(content), null)
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

test('parseClaude preserves pricing dimensions before global message deduplication', () => {
  const content = [
    JSON.stringify({ type: 'assistant', timestamp: '2026-08-24T12:00:00Z', message: { id: 'msg-1', model: 'claude-opus-5', usage: { input_tokens: 2, output_tokens: 10, cache_read_input_tokens: 20, cache_creation_input_tokens: 30, thinking_tokens: 4, cache_creation: { ephemeral_5m_input_tokens: 5, ephemeral_1h_input_tokens: 25 }, service_tier: 'standard', speed: 'standard', inference_geo: 'not_available' } } }),
    JSON.stringify({ type: 'assistant', timestamp: '2026-08-24T12:00:01Z', message: { id: 'msg-1', model: 'claude-opus-5', usage: { input_tokens: 2, output_tokens: 10, cache_read_input_tokens: 20, cache_creation_input_tokens: 30 } } }),
  ].join('\n')

  const records = parseClaude(content)

  assert.equal(records.length, 2)
  assert.deepEqual(records[0], {
    tool: 'claude', messageId: 'msg-1', date: '2026-08-24', model: 'claude-opus-5',
    occurredAtUtc: '2026-08-24T12:00:00.000Z', inputTokens: 2, outputTokens: 10,
    cacheReadTokens: 20, cacheWriteTokens: 30, cacheWrite1hTokens: 25,
    cacheWrite5mTokens: 5, thoughtTokens: 4, serviceTier: 'standard', speed: 'standard',
    inferenceGeo: 'not_available',
  })
})

test('parseClaude keeps sparse and rich copies for global message-id selection', () => {
  const content = [
    JSON.stringify({ type: 'assistant', timestamp: '2026-08-24T12:00:00Z', message: { id: 'msg-sparse-first', model: 'claude-opus-5', usage: { input_tokens: 2, output_tokens: 10, cache_creation_input_tokens: 30 } } }),
    JSON.stringify({ type: 'assistant', timestamp: '2026-08-24T12:00:01Z', message: { id: 'msg-sparse-first', model: 'claude-opus-5', usage: { input_tokens: 2, output_tokens: 10, cache_creation_input_tokens: 30, cache_creation: { ephemeral_5m_input_tokens: 5, ephemeral_1h_input_tokens: 25 }, service_tier: 'standard', speed: 'fast', inference_geo: 'us' } } }),
  ].join('\n')

  const records = parseClaude(content)
  const snapshots = buildDailySnapshots(records)

  assert.equal(records.length, 2)
  assert.equal(snapshots.length, 1)
  assert.equal(snapshots[0].eventKey, 'claude:2026-08-24:claude-opus-5:standard:fast:us')
  assert.equal(snapshots[0].cacheWrite1hTokens, 25)
})

test('parseClaude ignores valid JSON values that are not telemetry objects', () => {
  const content = ['null', '0', 'true', '"text"', '[]'].join('\n')

  assert.deepEqual(parseClaude(content), [])
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

test('buildDailySnapshots prefers a split-only richer Claude copy across files', () => {
  const sparse = JSON.stringify({ type: 'assistant', timestamp: '2026-08-24T12:00:00Z', message: { id: 'msg-split-only', model: 'claude-opus-5', usage: { input_tokens: 2, output_tokens: 10, cache_creation_input_tokens: 30 } } })
  const split = JSON.stringify({ type: 'assistant', timestamp: '2026-08-24T12:00:01Z', message: { id: 'msg-split-only', model: 'claude-opus-5', usage: { input_tokens: 2, output_tokens: 10, cache_creation_input_tokens: 30, cache_creation: { ephemeral_5m_input_tokens: 5, ephemeral_1h_input_tokens: 25 } } } })

  const snapshots = buildDailySnapshots([...parseClaude(sparse), ...parseClaude(split)])

  assert.equal(snapshots.length, 1)
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

test('parseKimi ignores valid JSON values that are not telemetry objects', () => {
  const content = ['null', '0', 'true', '"text"', '[]'].join('\n')

  assert.deepEqual(parseKimi(content), [])
})

test('local parsers ignore records without a valid observation timestamp', () => {
  const claude = parseClaude(JSON.stringify({ type: 'assistant', timestamp: null, message: { id: 'msg-no-time', model: 'claude-opus-5', usage: { input_tokens: 1, output_tokens: 1 } } }))
  const kimi = parseKimi(JSON.stringify({ type: 'usage.record', time: null, model: 'kimi-code/kimi-for-coding', usage: { inputOther: 1, output: 1 } }))

  assert.deepEqual(claude, [])
  assert.deepEqual(kimi, [])
})

test('buildDailySnapshots sums sessions into one stable cumulative day/model key', () => {
  const records = [
    { tool: 'codex', date: '2026-08-24', model: 'gpt-5.4', occurredAtUtc: '2026-08-24T10:00:00Z', cum: { input: 10, output: 2, cacheRead: 1, cacheWrite: 0 } },
    { tool: 'codex', date: '2026-08-24', model: 'gpt-5.4', occurredAtUtc: '2026-08-24T12:00:00Z', cum: { input: 20, output: 3, cacheRead: 2, cacheWrite: 0 } },
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
  const record = input => ({ tool: 'codex', date: '2026-08-24', model: 'gpt-5.4', cum: { input, output: 2, cacheRead: 1, cacheWrite: 0 } })

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
  assert.equal(JSON.parse(kimiSnapshot.rawPayload).note, undefined)
})

test('updateFileCache parses only changed paths and drops files deleted from the full scan', async () => {
  const cache = {
    same: { mtimeMs: 1, records: [{ id: 'same' }] },
    removed: { mtimeMs: 1, records: [{ id: 'removed' }] },
  }
  const reads = []
  const files = [{ path: 'same', mtimeMs: 1 }, { path: 'changed', mtimeMs: 2 }]

  const result = await updateFileCache(
    files,
    cache,
    content => [{ id: content }],
    async path => { reads.push(path); return path },
  )

  assert.deepEqual(reads, ['changed'])
  assert.deepEqual(result.records.map(x => x.id).sort(), ['changed', 'same'])
  assert.deepEqual(Object.keys(result.cache).sort(), ['changed', 'same'])
})

test('listJsonl discovers old and current transcripts so age never changes cumulative truth', async () => {
  const root = await mkdtemp(join(tmpdir(), 'observatory-sweep-'))
  const nested = join(root, 'nested')
  await mkdir(nested)
  const oldPath = join(root, 'old.jsonl')
  const currentPath = join(nested, 'current.jsonl')
  await writeFile(oldPath, '{}\n')
  await writeFile(currentPath, '{}\n')
  await utimes(oldPath, new Date('2020-01-01T00:00:00Z'), new Date('2020-01-01T00:00:00Z'))

  try {
    const files = await listJsonl(root)

    assert.deepEqual(files.map(file => file.path).sort(), [currentPath, oldPath].sort())
  } finally {
    await rm(root, { recursive: true, force: true })
  }
})

test('reconciliation sends a zero correction when the final transcript disappears', () => {
  const prior = buildDailySnapshots([
    { tool: 'codex', date: '2026-08-24', model: 'gpt-5.4', occurredAtUtc: '2026-08-24T12:00:00Z', cum: { input: 30, output: 5, cacheRead: 3, cacheWrite: 0 } },
  ])[0]

  const submissions = planSnapshotSubmissions([], { [prior.eventKey]: prior })

  assert.equal(submissions.length, 1)
  assert.equal(submissions[0].active, false)
  assert.deepEqual({
    eventKey: submissions[0].snapshot.eventKey,
    inputTokens: submissions[0].snapshot.inputTokens,
    outputTokens: submissions[0].snapshot.outputTokens,
    cacheReadTokens: submissions[0].snapshot.cacheReadTokens,
    cacheWriteTokens: submissions[0].snapshot.cacheWriteTokens,
    thoughtTokens: submissions[0].snapshot.thoughtTokens,
    costUsd: submissions[0].snapshot.costUsd,
  }, {
    eventKey: 'codex:2026-08-24:gpt-5.4',
    inputTokens: 0,
    outputTokens: 0,
    cacheReadTokens: 0,
    cacheWriteTokens: 0,
    thoughtTokens: 0,
    costUsd: 0,
  })
  assert.equal(JSON.parse(submissions[0].snapshot.rawPayload).tombstone, true)
})

test('successful disable reconciliation allows a source to be re-enabled cleanly', () => {
  const snapshot = buildDailySnapshots([
    { tool: 'kimi', date: '2026-08-24', model: 'kimi-code/kimi-for-coding', occurredAtUtc: '2026-08-24T12:00:00Z', inputTokens: 10, outputTokens: 2, cacheReadTokens: 20, cacheWriteTokens: 3 },
  ])[0]
  const emitted = { [snapshot.eventKey]: snapshot }
  const disabledSnapshots = parseLocalSources('codex').has('kimi') ? [snapshot] : []
  const [disabled] = planSnapshotSubmissions(disabledSnapshots, emitted)

  const afterDisable = recordSuccessfulSubmission(emitted, disabled)
  const reenabledSnapshots = parseLocalSources('kimi').has('kimi') ? [snapshot] : []
  const [reenabled] = planSnapshotSubmissions(reenabledSnapshots, afterDisable)

  assert.deepEqual(afterDisable, {})
  assert.equal(reenabled.active, true)
  assert.equal(reenabled.snapshot.eventKey, 'kimi:2026-08-24:kimi-code/kimi-for-coding')
  assert.equal(reenabled.snapshot.inputTokens, 10)
})

test('reconciliation corrects a daily snapshot after one transcript is deleted', () => {
  const record = input => ({
    tool: 'codex', date: '2026-08-24', model: 'gpt-5.4', occurredAtUtc: '2026-08-24T12:00:00Z',
    cum: { input, output: 2, cacheRead: 1, cacheWrite: 0 },
  })
  const prior = buildDailySnapshots([record(10), record(20)])[0]
  const current = buildDailySnapshots([record(10)])[0]

  const submissions = planSnapshotSubmissions([current], { [prior.eventKey]: prior })

  assert.equal(submissions.length, 1)
  assert.equal(submissions[0].active, true)
  assert.equal(submissions[0].snapshot.eventKey, 'codex:2026-08-24:gpt-5.4')
  assert.equal(submissions[0].snapshot.inputTokens, 10)
})

test('reconciliation clears the old key when a Claude pricing dimension changes', () => {
  const claude = speed => buildDailySnapshots(parseClaude(JSON.stringify({
    type: 'assistant',
    timestamp: '2026-08-24T12:00:00Z',
    message: {
      id: 'msg-dimension-change',
      model: 'claude-opus-5',
      usage: { input_tokens: 2, output_tokens: 10, service_tier: 'standard', speed, inference_geo: 'us' },
    },
  })))[0]
  const prior = claude('standard')
  const current = claude('fast')

  const submissions = planSnapshotSubmissions([current], { [prior.eventKey]: prior })
  const oldKey = submissions.find(item => !item.active)
  const newKey = submissions.find(item => item.active)

  assert.equal(submissions.length, 2)
  assert.equal(oldKey.snapshot.eventKey, 'claude:2026-08-24:claude-opus-5:standard:standard:us')
  assert.equal(oldKey.snapshot.inputTokens, 0)
  assert.equal(newKey.snapshot.eventKey, 'claude:2026-08-24:claude-opus-5:standard:fast:us')
  assert.equal(newKey.snapshot.inputTokens, 2)
})

test('losing emitted state only resubmits the current stable snapshot', () => {
  const snapshot = buildDailySnapshots([
    { tool: 'copilot', date: '2026-08-24', model: 'gpt-5.4', occurredAtUtc: '2026-08-24T12:00:00Z', cum: { input: 10, output: 2, cacheRead: 20, cacheWrite: 0 } },
  ])[0]

  const submissions = planSnapshotSubmissions([snapshot], {})

  assert.equal(submissions.length, 1)
  assert.equal(submissions[0].active, true)
  assert.equal(submissions[0].snapshot.eventKey, 'copilot:2026-08-24:gpt-5.4')
})

test('parseLocalSources defaults to every collector and honors an explicit allowlist', () => {
  assert.deepEqual([...parseLocalSources()].sort(), ['claude', 'codex', 'copilot', 'kimi'])
  assert.deepEqual([...parseLocalSources('codex,kimi')].sort(), ['codex', 'kimi'])
})
