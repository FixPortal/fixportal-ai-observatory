// Self-check for the local usage sweeper's pure logic. Zero deps:
//   node --test clients/observatory-sweep.test.mjs
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { execFile } from 'node:child_process'
import { mkdtemp, mkdir, readFile, rm, utimes, writeFile } from 'node:fs/promises'
import { createServer } from 'node:http'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { promisify } from 'node:util'
import {
  pickRates, costUsd, parseCodex, parseCopilot, parseClaude, parseKimi,
  buildDailySnapshots, updateFileCache, parseLocalSources, listJsonl,
  planSnapshotSubmissions, scanRecords,
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
  const line = JSON.stringify({ timestamp: '2026-08-24T12:00:00Z', type: 'event_msg', payload: { type: 'token_count', info: { total_token_usage: { input_tokens: 10, cached_input_tokens: 0, output_tokens: 5 } } } })
  assert.equal(parseCodex(line).model, 'gpt-5')
})

test('parseCodex ignores valid JSON values that are not telemetry objects', () => {
  const content = ['null', '0', 'true', '"text"', '[]'].join('\n')

  assert.equal(parseCodex(content), null)
})

test('parseCodex rejects token totals without a telemetry timestamp', () => {
  const line = JSON.stringify({ type: 'event_msg', payload: { type: 'token_count', info: { total_token_usage: { input_tokens: 10, output_tokens: 5 } } } })

  assert.equal(parseCodex(line), null)
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

test('parseCopilot rejects shutdown totals without a telemetry timestamp', () => {
  const shutdown = JSON.stringify({ type: 'session.shutdown', data: { modelMetrics: { 'gpt-5.4': { usage: { inputTokens: 10, outputTokens: 5 } } } } })

  assert.equal(parseCopilot(shutdown), null)
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

test('buildDailySnapshots keeps thought-only usage active', () => {
  const [snapshot] = buildDailySnapshots([{
    tool: 'claude',
    date: '2026-08-24',
    model: 'claude-opus-5',
    occurredAtUtc: '2026-08-24T12:00:00Z',
    inputTokens: 0,
    outputTokens: 0,
    cacheReadTokens: 0,
    cacheWriteTokens: 0,
    thoughtTokens: 7,
  }])

  assert.equal(snapshot.eventKey, 'claude:2026-08-24:claude-opus-5:unknown:unknown:unknown')
  assert.equal(snapshot.thoughtTokens, 7)
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

test('scanRecords rebuilds an unversioned matching-mtime cache instead of reusing stale records', async () => {
  const root = await mkdtemp(join(tmpdir(), 'observatory-sweep-upgrade-'))
  const sessions = join(root, 'codex', 'sessions')
  await mkdir(sessions, { recursive: true })
  const path = join(sessions, 'rollout.jsonl')
  await writeFile(path, JSON.stringify({
    type: 'event_msg',
    payload: { type: 'token_count', info: { total_token_usage: { input_tokens: 10, output_tokens: 5 } } },
  }))
  const [file] = await listJsonl(sessions)
  const stale = {
    tool: 'codex',
    date: '2030-01-01',
    model: 'gpt-5',
    occurredAtUtc: '2030-01-01T00:00:00.000Z',
    cum: { input: 10, output: 5, cacheRead: 0, cacheWrite: 0 },
  }
  const state = { files: { codex: { [path]: { mtimeMs: file.mtimeMs, records: [stale] } } } }
  const cfg = {
    codexHome: join(root, 'codex'),
    copilotHome: join(root, 'copilot'),
    claudeHome: join(root, 'claude'),
    kimiHome: join(root, 'kimi'),
  }

  try {
    const records = await scanRecords(cfg, state, new Set(['codex']))

    assert.deepEqual(records, [])
    assert.equal(state.parseCacheVersion, 1)
    assert.deepEqual(state.files.codex[path].records, [])
  } finally {
    await rm(root, { recursive: true, force: true })
  }
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

test('listJsonl tolerates only an absent top-level tool directory', async () => {
  const root = await mkdtemp(join(tmpdir(), 'observatory-sweep-missing-'))

  try {
    assert.deepEqual(await listJsonl(join(root, 'missing')), [])
  } finally {
    await rm(root, { recursive: true, force: true })
  }
})

test('touching and restoring a transcript cannot move usage to another day', async () => {
  const root = await mkdtemp(join(tmpdir(), 'observatory-sweep-touch-'))
  const sessions = join(root, 'codex', 'sessions')
  await mkdir(sessions, { recursive: true })
  const path = join(sessions, 'rollout.jsonl')
  await writeFile(path, JSON.stringify({
    timestamp: '2026-08-24T12:00:00Z',
    type: 'event_msg',
    payload: { type: 'token_count', info: { total_token_usage: { input_tokens: 10, output_tokens: 5 } } },
  }))
  const cfg = {
    codexHome: join(root, 'codex'),
    copilotHome: join(root, 'copilot'),
    claudeHome: join(root, 'claude'),
    kimiHome: join(root, 'kimi'),
  }
  const state = {}

  try {
    const before = await scanRecords(cfg, state, new Set(['codex']))
    await utimes(path, new Date('2030-01-01T00:00:00Z'), new Date('2030-01-01T00:00:00Z'))
    const touched = await scanRecords(cfg, state, new Set(['codex']))
    await utimes(path, new Date('2020-01-01T00:00:00Z'), new Date('2020-01-01T00:00:00Z'))
    const restored = await scanRecords(cfg, state, new Set(['codex']))

    assert.deepEqual([before[0].date, touched[0].date, restored[0].date], [
      '2026-08-24', '2026-08-24', '2026-08-24',
    ])
    assert.deepEqual([before[0].occurredAtUtc, touched[0].occurredAtUtc, restored[0].occurredAtUtc], [
      '2026-08-24T12:00:00Z', '2026-08-24T12:00:00Z', '2026-08-24T12:00:00Z',
    ])
  } finally {
    await rm(root, { recursive: true, force: true })
  }
})

test('server inventory clears a deleted final snapshot after local state loss', () => {
  const prior = buildDailySnapshots([
    { tool: 'codex', date: '2026-08-24', model: 'gpt-5.4', occurredAtUtc: '2026-08-24T12:00:00Z', cum: { input: 30, output: 5, cacheRead: 3, cacheWrite: 0 } },
  ])[0]

  const submissions = planSnapshotSubmissions([], [prior])

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

test('server inventory clears a disabled source after state loss and allows re-enable', () => {
  const snapshot = buildDailySnapshots([
    { tool: 'kimi', date: '2026-08-24', model: 'kimi-code/kimi-for-coding', occurredAtUtc: '2026-08-24T12:00:00Z', inputTokens: 10, outputTokens: 2, cacheReadTokens: 20, cacheWriteTokens: 3 },
  ])[0]
  const disabledSnapshots = parseLocalSources('codex').has('kimi') ? [snapshot] : []
  const [disabled] = planSnapshotSubmissions(disabledSnapshots, [snapshot])

  const reenabledSnapshots = parseLocalSources('kimi').has('kimi') ? [snapshot] : []
  const [reenabled] = planSnapshotSubmissions(reenabledSnapshots, [])

  assert.equal(disabled.active, false)
  assert.equal(disabled.snapshot.inputTokens, 0)
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

  const submissions = planSnapshotSubmissions([current], [prior])

  assert.equal(submissions.length, 1)
  assert.equal(submissions[0].active, true)
  assert.equal(submissions[0].snapshot.eventKey, 'codex:2026-08-24:gpt-5.4')
  assert.equal(submissions[0].snapshot.inputTokens, 10)
})

test('server inventory clears the old Claude dimension key after local state loss', () => {
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

  const submissions = planSnapshotSubmissions([current], [prior])
  const oldKey = submissions.find(item => !item.active)
  const newKey = submissions.find(item => item.active)

  assert.equal(submissions.length, 2)
  assert.equal(oldKey.snapshot.eventKey, 'claude:2026-08-24:claude-opus-5:standard:standard:us')
  assert.equal(oldKey.snapshot.inputTokens, 0)
  assert.equal(newKey.snapshot.eventKey, 'claude:2026-08-24:claude-opus-5:standard:fast:us')
  assert.equal(newKey.snapshot.inputTokens, 2)
})

test('replacement plans active snapshots before tombstones in both lexical key directions', () => {
  const snapshot = (sourceId, eventKey) => ({
    provider: 'openai', model: 'gpt-5.4', inputTokens: 1, outputTokens: 0,
    cacheReadTokens: 0, cacheWriteTokens: 0, thoughtTokens: 0, costUsd: 0,
    occurredAtUtc: '2026-08-24T12:00:00Z', runtime: 'codex', sourceId,
    sourceKind: 'localTelemetry', usageScope: 'subscription', costBasis: 'notional', eventKey,
  })

  for (const [oldKey, newKey] of [['a-old', 'z-new'], ['z-old', 'a-new']]) {
    const submissions = planSnapshotSubmissions(
      [snapshot('codex-local', newKey)],
      [snapshot('codex-local', oldKey)],
    )

    assert.deepEqual(submissions.map(item => [item.active, item.snapshot.eventKey]), [
      [true, newKey],
      [false, oldKey],
    ])
  }
})

test('parseLocalSources defaults to every collector and honors an explicit allowlist', () => {
  assert.deepEqual([...parseLocalSources()].sort(), ['claude', 'codex', 'copilot', 'kimi'])
  assert.deepEqual([...parseLocalSources('codex,kimi')].sort(), ['codex', 'kimi'])
})

test('main retries a failed server-inventory tombstone from persisted state and then succeeds', async () => {
  const root = await mkdtemp(join(tmpdir(), 'observatory-sweep-main-'))
  const statePath = join(root, 'state', 'sweep.json')
  const posts = []
  const gets = []
  let inventoryActive = true
  const prior = {
    provider: 'anthropic',
    occurredAtUtc: '2026-08-24T12:00:00Z',
    model: 'claude-opus-5',
    inputTokens: 2,
    outputTokens: 10,
    cacheReadTokens: 0,
    cacheWriteTokens: 0,
    cacheWrite1hTokens: 0,
    thoughtTokens: 0,
    costUsd: 0.01,
    runtime: 'claude',
    sourceId: 'claude-local',
    sourceKind: 'localTelemetry',
    usageScope: 'subscription',
    costBasis: 'notional',
    eventKey: 'claude:2026-08-24:claude-opus-5:standard:standard:us',
  }
  const server = createServer(async (request, response) => {
    const url = new URL(request.url, 'http://127.0.0.1')
    if (request.method === 'GET' && url.pathname === '/api/events/local-snapshots') {
      const sourceId = url.searchParams.get('sourceId')
      gets.push({ sourceId, apiKey: request.headers['x-observatory-key'] })
      const body = inventoryActive && sourceId === 'claude-local' ? [prior] : []
      response.writeHead(200, { 'Content-Type': 'application/json' }).end(JSON.stringify(body))
      return
    }
    if (request.method === 'POST' && url.pathname === '/api/events') {
      let body = ''
      for await (const chunk of request) { body += chunk }
      posts.push(JSON.parse(body))
      if (posts.length === 1) {
        response.writeHead(500).end()
      } else {
        inventoryActive = false
        response.writeHead(200, { 'Content-Type': 'application/json' }).end('{}')
      }
      return
    }
    response.writeHead(404).end()
  })
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve))
  const address = server.address()
  const run = promisify(execFile)
  const env = {
    ...process.env,
    OBSERVATORY_URL: `http://127.0.0.1:${address.port}`,
    OBSERVATORY_API_KEY: 'test-key',
    OBSERVATORY_STATE: statePath,
    OBSERVATORY_LOCAL_SOURCES: 'codex',
    CODEX_HOME: join(root, 'codex'),
    COPILOT_HOME: join(root, 'copilot'),
    CLAUDE_HOME: join(root, 'claude'),
    KIMI_HOME: join(root, 'kimi'),
  }

  try {
    await run(process.execPath, [fileURLToPath(new URL('./observatory-sweep.mjs', import.meta.url))], { env })
    const persistedAfterFailure = JSON.parse(await readFile(statePath, 'utf8'))
    await run(process.execPath, [fileURLToPath(new URL('./observatory-sweep.mjs', import.meta.url))], { env })

    assert.deepEqual(persistedAfterFailure.files.codex, {})
    assert.equal(gets.length, 8)
    assert.deepEqual([...new Set(gets.map(request => request.sourceId))].sort(), [
      'claude-local', 'codex-local', 'copilot-local', 'kimi-local',
    ])
    assert.equal(gets.every(request => request.apiKey === 'test-key'), true)
    assert.equal(posts.length, 2)
    assert.deepEqual(posts.map(body => ({
      eventKey: body.eventKey,
      sourceId: body.sourceId,
      inputTokens: body.inputTokens,
      outputTokens: body.outputTokens,
    })), [
      { eventKey: prior.eventKey, sourceId: 'claude-local', inputTokens: 0, outputTokens: 0 },
      { eventKey: prior.eventKey, sourceId: 'claude-local', inputTokens: 0, outputTokens: 0 },
    ])
  } finally {
    await new Promise(resolve => server.close(resolve))
    await rm(root, { recursive: true, force: true })
  }
})

test('main withholds a source tombstone after its replacement fails but continues unrelated sources', async () => {
  const root = await mkdtemp(join(tmpdir(), 'observatory-sweep-replacement-'))
  const projects = join(root, 'claude', 'projects')
  const statePath = join(root, 'state', 'sweep.json')
  await mkdir(projects, { recursive: true })
  await writeFile(join(projects, 'session.jsonl'), JSON.stringify({
    type: 'assistant',
    timestamp: '2026-08-24T12:00:00Z',
    message: {
      id: 'msg-replacement',
      model: 'claude-opus-5',
      usage: { input_tokens: 2, output_tokens: 10, service_tier: 'standard', speed: 'zzz', inference_geo: 'us' },
    },
  }))
  const oldClaude = {
    provider: 'anthropic', occurredAtUtc: '2026-08-24T12:00:00Z', model: 'claude-opus-5',
    costUsd: 0.01, runtime: 'claude', sourceId: 'claude-local', sourceKind: 'localTelemetry',
    usageScope: 'subscription', costBasis: 'notional',
    eventKey: 'claude:2026-08-24:claude-opus-5:standard:aaa:us',
  }
  const oldKimi = {
    provider: 'moonshot', occurredAtUtc: '2026-08-24T12:00:00Z', model: 'kimi-code/kimi-for-coding',
    costUsd: null, runtime: 'kimi', sourceId: 'kimi-local', sourceKind: 'localTelemetry',
    usageScope: 'subscription', costBasis: 'notional', eventKey: 'kimi:2026-08-24:kimi-code/kimi-for-coding',
  }
  const currentKey = 'claude:2026-08-24:claude-opus-5:standard:zzz:us'
  const posts = []
  const server = createServer(async (request, response) => {
    const url = new URL(request.url, 'http://127.0.0.1')
    if (request.method === 'GET' && url.pathname === '/api/events/local-snapshots') {
      const sourceId = url.searchParams.get('sourceId')
      const body = sourceId === 'claude-local' ? [oldClaude] : sourceId === 'kimi-local' ? [oldKimi] : []
      response.writeHead(200, { 'Content-Type': 'application/json' }).end(JSON.stringify(body))
      return
    }
    if (request.method === 'POST' && url.pathname === '/api/events') {
      let body = ''
      for await (const chunk of request) { body += chunk }
      const parsed = JSON.parse(body)
      posts.push(parsed)
      response.writeHead(parsed.eventKey === currentKey ? 500 : 200, { 'Content-Type': 'application/json' }).end('{}')
      return
    }
    response.writeHead(404).end()
  })
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve))
  const address = server.address()
  const run = promisify(execFile)
  const env = {
    ...process.env,
    OBSERVATORY_URL: `http://127.0.0.1:${address.port}`,
    OBSERVATORY_API_KEY: 'test-key',
    OBSERVATORY_STATE: statePath,
    OBSERVATORY_LOCAL_SOURCES: 'claude',
    CODEX_HOME: join(root, 'codex'),
    COPILOT_HOME: join(root, 'copilot'),
    CLAUDE_HOME: join(root, 'claude'),
    KIMI_HOME: join(root, 'kimi'),
  }

  try {
    await run(process.execPath, [fileURLToPath(new URL('./observatory-sweep.mjs', import.meta.url))], { env })

    assert.deepEqual(posts.map(body => body.eventKey), [currentKey, oldKimi.eventKey])
    assert.equal(posts.some(body => body.eventKey === oldClaude.eventKey), false)
  } finally {
    await new Promise(resolve => server.close(resolve))
    await rm(root, { recursive: true, force: true })
  }
})

test('main aborts nested discovery failures without replacing cache or posting partial truth', async () => {
  const root = await mkdtemp(join(tmpdir(), 'observatory-sweep-read-error-'))
  const statePath = join(root, 'state', 'sweep.json')
  const originalState = JSON.stringify({
    parseCacheVersion: 1,
    files: { codex: { sentinel: { mtimeMs: 1, records: [{ id: 'keep' }] } } },
  })
  await mkdir(join(root, 'state'), { recursive: true })
  const posts = []
  const server = createServer(async (request, response) => {
    const url = new URL(request.url, 'http://127.0.0.1')
    if (request.method === 'GET' && url.pathname === '/api/events/local-snapshots') {
      response.writeHead(200, { 'Content-Type': 'application/json' }).end('[]')
      return
    }
    if (request.method === 'POST' && url.pathname === '/api/events') {
      posts.push(url.pathname)
      response.writeHead(200, { 'Content-Type': 'application/json' }).end('{}')
      return
    }
    response.writeHead(404).end()
  })
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve))
  const address = server.address()
  const envKeys = [
    'OBSERVATORY_URL', 'OBSERVATORY_API_KEY', 'OBSERVATORY_STATE', 'OBSERVATORY_LOCAL_SOURCES',
    'CODEX_HOME', 'COPILOT_HOME', 'CLAUDE_HOME', 'KIMI_HOME',
  ]
  const priorEnv = Object.fromEntries(envKeys.map(key => [key, process.env[key]]))
  Object.assign(process.env, {
    OBSERVATORY_URL: `http://127.0.0.1:${address.port}`,
    OBSERVATORY_API_KEY: 'test-key',
    OBSERVATORY_STATE: statePath,
    OBSERVATORY_LOCAL_SOURCES: 'codex',
    CODEX_HOME: join(root, 'codex'),
    COPILOT_HOME: join(root, 'copilot'),
    CLAUDE_HOME: join(root, 'claude'),
    KIMI_HOME: join(root, 'kimi'),
  })

  try {
    const { main } = await import('./observatory-sweep.mjs')
    for (const code of ['EACCES', 'EIO', 'ENOENT']) {
      await writeFile(statePath, originalState)
      const discover = dir => listJsonl(dir, [], {
        readdir: async path => {
          if (path === dir) { return [{ name: 'nested', isDirectory: () => true }] }
          const error = new Error(`synthetic nested ${code}`)
          error.code = code
          throw error
        },
        stat: async () => ({ mtimeMs: 1 }),
      })

      await assert.rejects(main({ discover }), error => error.code === code)
      assert.equal(await readFile(statePath, 'utf8'), originalState)
    }

    assert.deepEqual(posts, [])
  } finally {
    for (const key of envKeys) {
      if (priorEnv[key] === undefined) { delete process.env[key] } else { process.env[key] = priorEnv[key] }
    }
    await new Promise(resolve => server.close(resolve))
    await rm(root, { recursive: true, force: true })
  }
})
