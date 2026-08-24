#!/usr/bin/env node
// AI Observatory local usage sweeper (drop-in).
//
// Rebuilds cumulative daily/model snapshots from local Codex, Copilot, Claude,
// and Kimi telemetry, then POSTs them to `/api/events`. The state file caches
// parsed files by path + mtime; server inventory makes losing it harmless.
//
// Zero dependencies: Node 18+ only (global fetch, fs/promises).

import { readFile, readdir, mkdir, writeFile, stat } from 'node:fs/promises'
import { homedir } from 'node:os'
import { join, dirname, basename } from 'node:path'
import { pathToFileURL } from 'node:url'

// Subscription-billed tools have no real per-token price. These rates preserve
// the existing notional comparison until first-party pricing renewal replaces it.
const OPENAI_PRICING = {
  'gpt-5.5': [1.25, 10.00, 0.125, 0],
  'gpt-5.4': [1.25, 10.00, 0.125, 0],
  'gpt-5': [1.25, 10.00, 0.125, 0],
  'o3': [10.00, 40.00, 2.50, 0],
  'o4-mini': [1.10, 4.40, 0.275, 0],
  'gpt-4.1-mini': [0.40, 1.60, 0.10, 0],
  'gpt-4.1': [2.00, 8.00, 0.50, 0],
  'gpt-4o-mini': [0.15, 0.60, 0.075, 0],
  'gpt-4o': [2.50, 10.00, 1.25, 0],
}
const OPENAI_DEFAULT = [1.25, 10.00, 0.125, 0]

const COPILOT_PRICING = {
  'gpt-5.4': [1.25, 10.00, 0.125, 0],
  'gpt-5': [1.25, 10.00, 0.125, 0],
  'gpt-4.1': [2.00, 8.00, 0.50, 0],
  'gpt-4o': [2.50, 10.00, 1.25, 0],
  'claude-opus-4': [15.00, 75.00, 1.50, 3.75],
  'claude-sonnet-4': [3.00, 15.00, 0.30, 0.75],
  'claude-haiku-4': [0.80, 4.00, 0.08, 0.20],
}
const COPILOT_DEFAULT = [2.00, 8.00, 0.50, 0]
const ALL_LOCAL_SOURCES = ['codex', 'copilot', 'claude', 'kimi']
const ALL_LOCAL_SOURCE_IDS = ALL_LOCAL_SOURCES.map(source => `${source}-local`)

// --- Pure helpers -----------------------------------------------------------

/** Longest-prefix model match so gpt-4o-mini resolves before gpt-4o. */
export function pickRates(table, model, fallback) {
  const m = (model ?? '').toLowerCase()
  let best = null
  for (const key of Object.keys(table)) {
    if (m.startsWith(key) && (best === null || key.length > best.length)) { best = key }
  }
  return best ? table[best] : fallback
}

/** Cost in USD from token totals and a [input, output, cacheRead, cacheWrite] rate row. */
export function costUsd(rates, d) {
  const c = (d.input * rates[0] + d.output * rates[1] + d.cacheRead * rates[2] + d.cacheWrite * rates[3]) / 1e6
  return Math.round(c * 1e8) / 1e8
}

function token(value) {
  return Math.max(0, Number.isFinite(Number(value)) ? Number(value) : 0)
}

function isoTimestamp(value) {
  if (value === null || value === undefined || value === '') { return null }
  const date = new Date(value)
  return Number.isNaN(date.valueOf()) ? null : date.toISOString()
}

/**
 * Parse a Codex rollout into its last cumulative token_count.
 *
 * ponytail: a session that switches model mid-flight is attributed wholly to the
 * last turn_context model -- the cumulative total isn't broken down per model.
 * Upgrade path: bucket token_count deltas by the preceding turn_context if/when
 * mixed-model Codex sessions become common.
 */
export function parseCodex(content) {
  let model = null
  let total = null
  let reasoning = 0
  let endedAt = null
  for (const line of content.split('\n')) {
    if (!line) { continue }
    let row
    try { row = JSON.parse(line) } catch { continue }
    if (!row || typeof row !== 'object') { continue }
    const payload = row.payload
    if (row.type === 'turn_context' && payload?.model) { model = payload.model }
    if (payload?.type === 'token_count' && payload.info?.total_token_usage && isoTimestamp(row.timestamp)) {
      total = payload.info.total_token_usage
      reasoning = token(total.reasoning_output_tokens)
      endedAt = row.timestamp
    }
  }
  if (!total) { return null }
  const cacheRead = token(total.cached_input_tokens)
  return {
    model: model ?? 'gpt-5',
    cum: {
      input: Math.max(0, token(total.input_tokens) - cacheRead),
      output: token(total.output_tokens),
      cacheRead,
      cacheWrite: 0,
    },
    reasoning,
    endedAt,
  }
}

/** Parse the last cumulative per-model Copilot session.shutdown event. */
export function parseCopilot(content) {
  let shutdown = null
  for (const line of content.split('\n')) {
    if (!line || !line.includes('"session.shutdown"')) { continue }
    try { shutdown = JSON.parse(line) } catch { /* keep last parseable */ }
  }
  const metrics = shutdown?.data?.modelMetrics
  const endedAt = isoTimestamp(shutdown?.timestamp)
  if (!metrics || !endedAt) { return null }
  const perModel = {}
  for (const [model, value] of Object.entries(metrics)) {
    const usage = value?.usage
    if (!usage) { continue }
    const cacheRead = token(usage.cacheReadTokens)
    perModel[model] = {
      input: Math.max(0, token(usage.inputTokens) - cacheRead),
      output: token(usage.outputTokens),
      cacheRead,
      cacheWrite: token(usage.cacheWriteTokens),
      reasoning: token(usage.reasoningTokens),
    }
  }
  return { endedAt, perModel }
}

/** Parse Claude assistant-message usage for global message.id selection. */
export function parseClaude(content) {
  const records = []
  for (const line of content.split('\n')) {
    if (!line) { continue }
    let row
    try { row = JSON.parse(line) } catch { continue }
    if (!row || typeof row !== 'object') { continue }
    const message = row.type === 'assistant' ? row.message : null
    const usage = message?.usage
    const occurredAtUtc = isoTimestamp(row.timestamp)
    if (!usage || !occurredAtUtc) { continue }

    const creation = usage.cache_creation ?? {}
    const cacheWrite5mTokens = token(creation.ephemeral_5m_input_tokens)
    const cacheWrite1hTokens = token(creation.ephemeral_1h_input_tokens)
    const cacheWriteTokens = Math.max(
      token(usage.cache_creation_input_tokens),
      cacheWrite5mTokens + cacheWrite1hTokens,
    )
    records.push({
      tool: 'claude',
      ...(message.id ? { messageId: message.id } : {}),
      date: occurredAtUtc.slice(0, 10),
      model: message.model ?? 'unknown',
      occurredAtUtc,
      inputTokens: token(usage.input_tokens),
      outputTokens: token(usage.output_tokens),
      cacheReadTokens: token(usage.cache_read_input_tokens),
      cacheWriteTokens,
      ...(Object.hasOwn(creation, 'ephemeral_1h_input_tokens') ? { cacheWrite1hTokens } : {}),
      ...(Object.hasOwn(creation, 'ephemeral_5m_input_tokens') ? { cacheWrite5mTokens } : {}),
      ...(Object.hasOwn(usage, 'thinking_tokens') || Object.hasOwn(usage, 'thinking_output_tokens')
        ? { thoughtTokens: token(usage.thinking_tokens ?? usage.thinking_output_tokens) }
        : {}),
      ...(Object.hasOwn(usage, 'service_tier') ? { serviceTier: usage.service_tier ?? 'unknown' } : {}),
      ...(Object.hasOwn(usage, 'speed') ? { speed: usage.speed ?? 'unknown' } : {}),
      ...(Object.hasOwn(usage, 'inference_geo') ? { inferenceGeo: usage.inference_geo ?? 'unknown' } : {}),
    })
  }
  return records
}

/** Parse only Kimi usage.record rows; step.end mirrors are intentionally ignored. */
export function parseKimi(content) {
  const records = []
  for (const line of content.split('\n')) {
    if (!line) { continue }
    let row
    try { row = JSON.parse(line) } catch { continue }
    if (!row || typeof row !== 'object') { continue }
    if (row.type !== 'usage.record' || !row.usage) { continue }
    const occurredAtUtc = isoTimestamp(row.time)
    if (!occurredAtUtc) { continue }
    records.push({
      tool: 'kimi',
      date: occurredAtUtc.slice(0, 10),
      model: row.model ?? 'kimi-code/kimi-for-coding',
      occurredAtUtc,
      inputTokens: token(row.usage.inputOther),
      outputTokens: token(row.usage.output),
      cacheReadTokens: token(row.usage.inputCacheRead),
      cacheWriteTokens: token(row.usage.inputCacheCreation),
    })
  }
  return records
}

function sourceMetadata(tool) {
  switch (tool) {
    case 'codex': return { provider: 'OpenAI', sourceId: 'codex-local', runtime: 'codex' }
    case 'copilot': return { provider: 'Copilot', sourceId: 'copilot-local', runtime: 'copilot' }
    case 'claude': return { provider: 'Anthropic', sourceId: 'claude-local', runtime: 'claude' }
    case 'kimi': return { provider: 'Moonshot', sourceId: 'kimi-local', runtime: 'kimi' }
    default: return null
  }
}

function recordTokens(record) {
  const cumulative = record.cum
  return {
    input: token(cumulative?.input ?? record.inputTokens),
    output: token(cumulative?.output ?? record.outputTokens),
    cacheRead: token(cumulative?.cacheRead ?? record.cacheReadTokens),
    cacheWrite: token(cumulative?.cacheWrite ?? record.cacheWriteTokens),
    cacheWrite1h: token(record.cacheWrite1hTokens),
    thought: token(record.thoughtTokens ?? record.reasoning ?? cumulative?.reasoning),
  }
}

function claudeRecordScore(record) {
  return ['serviceTier', 'speed', 'inferenceGeo', 'cacheWrite1hTokens', 'cacheWrite5mTokens', 'thoughtTokens']
    .filter(key => Object.hasOwn(record, key)).length
}

function deduplicateClaudeRecords(records) {
  const deduplicated = []
  const indexes = new Map()
  for (const record of records) {
    if (record.tool !== 'claude' || !record.messageId) {
      deduplicated.push(record)
      continue
    }
    const index = indexes.get(record.messageId)
    if (index === undefined) {
      indexes.set(record.messageId, deduplicated.length)
      deduplicated.push(record)
      continue
    }
    const existing = deduplicated[index]
    const score = claudeRecordScore(record)
    const existingScore = claudeRecordScore(existing)
    if (score > existingScore || (score === existingScore && record.occurredAtUtc < existing.occurredAtUtc)) {
      deduplicated[index] = record
    }
  }
  return deduplicated
}

/** Rebuild stable cumulative day/model snapshots from cached per-file records. */
export function buildDailySnapshots(records) {
  const groups = new Map()
  for (const record of deduplicateClaudeRecords(records)) {
    const metadata = sourceMetadata(record.tool)
    if (!metadata || !record.date || !record.model) { continue }

    const tier = record.serviceTier ?? 'unknown'
    const speed = record.speed ?? 'unknown'
    const geo = record.inferenceGeo ?? 'unknown'
    const eventKey = record.tool === 'claude'
      ? `claude:${record.date}:${record.model}:${tier}:${speed}:${geo}`
      : `${record.tool}:${record.date}:${record.model}`
    let group = groups.get(eventKey)
    if (!group) {
      group = {
        ...metadata,
        tool: record.tool,
        date: record.date,
        model: record.model,
        eventKey,
        serviceTier: tier,
        speed,
        inferenceGeo: geo,
        input: 0,
        output: 0,
        cacheRead: 0,
        cacheWrite: 0,
        cacheWrite1h: 0,
        cacheWrite5m: 0,
        thought: 0,
        occurredAtUtc: `${record.date}T00:00:00.000Z`,
      }
      groups.set(eventKey, group)
    }

    const usage = recordTokens(record)
    group.input += usage.input
    group.output += usage.output
    group.cacheRead += usage.cacheRead
    group.cacheWrite += usage.cacheWrite
    group.cacheWrite1h += usage.cacheWrite1h
    group.cacheWrite5m += token(record.cacheWrite5mTokens)
    group.thought += usage.thought
    const occurredAtUtc = isoTimestamp(record.occurredAtUtc)
    if (occurredAtUtc && occurredAtUtc > group.occurredAtUtc) { group.occurredAtUtc = occurredAtUtc }
  }

  return [...groups.values()]
    .filter(group => group.input + group.output + group.cacheRead + group.cacheWrite > 0)
    .sort((a, b) => a.eventKey.localeCompare(b.eventKey))
    .map(group => {
      const totals = {
        input: group.input,
        output: group.output,
        cacheRead: group.cacheRead,
        cacheWrite: group.cacheWrite,
      }
      const notionalCost = group.tool === 'codex'
        ? costUsd(pickRates(OPENAI_PRICING, group.model, OPENAI_DEFAULT), totals)
        : group.tool === 'copilot'
          ? costUsd(pickRates(COPILOT_PRICING, group.model, COPILOT_DEFAULT), totals)
          : null
      return {
        provider: group.provider,
        model: group.model,
        inputTokens: group.input,
        outputTokens: group.output,
        cacheReadTokens: group.cacheRead,
        cacheWriteTokens: group.cacheWrite,
        cacheWrite1hTokens: group.cacheWrite1h,
        thoughtTokens: group.thought,
        costUsd: notionalCost,
        eventKey: group.eventKey,
        occurredAtUtc: group.occurredAtUtc,
        sourceId: group.sourceId,
        sourceKind: 'localTelemetry',
        usageScope: 'subscription',
        costBasis: 'notional',
        runtime: group.runtime,
        rawPayload: JSON.stringify({
          source: 'observatory-sweep',
          tool: group.tool,
          service_tier: group.serviceTier,
          speed: group.speed,
          inference_geo: group.inferenceGeo,
          thinking_tokens: group.thought,
          cache_creation: {
            ephemeral_5m_input_tokens: group.cacheWrite5m,
            ephemeral_1h_input_tokens: group.cacheWrite1h,
          },
          ...(notionalCost === null ? {} : {
            note: 'costUsd is notional - local coding telemetry is subscription-billed',
          }),
        }),
      }
    })
}

function zeroSnapshot(snapshot) {
  return {
    ...snapshot,
    inputTokens: 0,
    outputTokens: 0,
    cacheReadTokens: 0,
    cacheWriteTokens: 0,
    cacheWrite1hTokens: 0,
    thoughtTokens: 0,
    costUsd: snapshot.costUsd === null ? null : 0,
    rawPayload: JSON.stringify({
      source: 'observatory-sweep',
      tool: snapshot.runtime,
      tombstone: true,
    }),
  }
}

/** Plan current snapshots plus zero corrections for server keys that vanished locally. */
export function planSnapshotSubmissions(snapshots, inventory = []) {
  const identity = snapshot => `${snapshot.sourceId}\n${snapshot.eventKey}`
  const currentKeys = new Set(snapshots.map(identity))
  const submissions = snapshots.map(snapshot => ({ snapshot, active: true }))
  for (const snapshot of Object.values(inventory)) {
    if (!currentKeys.has(identity(snapshot))) {
      // ponytail: /api/events has correction but no deletion. Zero corrections
      // leave a one-request ceiling per removed key; add deletion only if request counts need exact removal.
      submissions.push({ snapshot: zeroSnapshot(snapshot), active: false })
    }
  }
  return submissions.sort((a, b) => a.snapshot.eventKey.localeCompare(b.snapshot.eventKey))
}

/** Parse changed files only and return a cache containing exactly the active scan. */
export async function updateFileCache(files, cache, parse, read = path => readFile(path, 'utf8')) {
  const next = {}
  const records = []
  for (const file of files) {
    const cached = cache?.[file.path]
    if (cached?.mtimeMs === file.mtimeMs && Array.isArray(cached.records)) {
      next[file.path] = cached
    } else {
      const parsed = await parse(await read(file.path), file)
      next[file.path] = { mtimeMs: file.mtimeMs, records: parsed ?? [] }
    }
    records.push(...next[file.path].records)
  }
  return { cache: next, records }
}

export function parseLocalSources(value) {
  const selected = value === undefined
    ? ALL_LOCAL_SOURCES
    : value.split(',').map(x => x.trim().toLowerCase()).filter(Boolean)
  return new Set(selected.filter(x => ALL_LOCAL_SOURCES.includes(x)))
}

// --- IO / orchestration -----------------------------------------------------

const VERBOSE = process.argv.includes('--verbose')
const DRY_RUN = process.argv.includes('--dry-run')
const log = (...args) => { if (VERBOSE) { console.error(...args) } }

async function loadState(path) {
  try { return JSON.parse(await readFile(path, 'utf8')) }
  catch { return {} }
}

async function saveState(path, state) {
  if (DRY_RUN) { return }
  await mkdir(dirname(path), { recursive: true })
  await writeFile(path, JSON.stringify(state, null, 2), 'utf8')
}

async function postEvent(url, apiKey, body) {
  if (DRY_RUN) { log('DRYRUN would post:', JSON.stringify(body)); return true }
  try {
    const response = await fetch(`${url}/api/events`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-Observatory-Key': apiKey },
      body: JSON.stringify(body),
      signal: AbortSignal.timeout(10_000),
    })
    if (!response.ok) { log(`POST ${response.status} for ${body.eventKey}`); return false }
    return true
  } catch (error) {
    log(`POST failed for ${body.eventKey}:`, error.message)
    return false
  }
}

async function fetchSnapshotInventory(url, apiKey) {
  const inventory = []
  for (const sourceId of ALL_LOCAL_SOURCE_IDS) {
    const response = await fetch(
      `${url}/api/events/local-snapshots?sourceId=${encodeURIComponent(sourceId)}`,
      {
        headers: { 'X-Observatory-Key': apiKey },
        signal: AbortSignal.timeout(10_000),
      },
    )
    if (!response.ok) { throw new Error(`Inventory GET ${response.status} for ${sourceId}`) }
    const snapshots = await response.json()
    if (!Array.isArray(snapshots)
      || snapshots.some(snapshot => !snapshot || typeof snapshot !== 'object'
        || snapshot.sourceId !== sourceId || typeof snapshot.eventKey !== 'string')) {
      throw new Error(`Invalid inventory response for ${sourceId}`)
    }
    inventory.push(...snapshots)
  }
  return inventory
}

export async function listJsonl(dir, out = []) {
  let entries
  try { entries = await readdir(dir, { withFileTypes: true }) } catch { return out }
  for (const entry of entries) {
    const full = join(dir, entry.name)
    if (entry.isDirectory()) { await listJsonl(full, out) }
    else if (entry.name.endsWith('.jsonl')) {
      const details = await stat(full)
      out.push({ path: full, mtimeMs: details.mtimeMs })
    }
  }
  return out
}

export async function scanRecords(cfg, state, enabled) {
  const records = []
  state.files ??= {}

  if (enabled.has('codex')) {
    const files = await listJsonl(join(cfg.codexHome, 'sessions'))
    const result = await updateFileCache(files, state.files.codex, content => {
      const parsed = parseCodex(content)
      if (!parsed) { return [] }
      const occurredAtUtc = parsed.endedAt
      return [{
        tool: 'codex',
        date: occurredAtUtc.slice(0, 10),
        model: parsed.model,
        occurredAtUtc,
        cum: parsed.cum,
        thoughtTokens: parsed.reasoning,
      }]
    })
    state.files.codex = result.cache
    records.push(...result.records)
  }

  if (enabled.has('copilot')) {
    const files = (await listJsonl(join(cfg.copilotHome, 'session-state')))
      .filter(file => basename(file.path) === 'events.jsonl')
    const result = await updateFileCache(files, state.files.copilot, content => {
      const parsed = parseCopilot(content)
      if (!parsed) { return [] }
      const occurredAtUtc = parsed.endedAt
      return Object.entries(parsed.perModel).map(([model, cum]) => ({
        tool: 'copilot',
        date: occurredAtUtc.slice(0, 10),
        model,
        occurredAtUtc,
        cum,
        thoughtTokens: cum.reasoning,
      }))
    })
    state.files.copilot = result.cache
    records.push(...result.records)
  }

  if (enabled.has('claude')) {
    const files = await listJsonl(join(cfg.claudeHome, 'projects'))
    const result = await updateFileCache(files, state.files.claude, content => parseClaude(content))
    state.files.claude = result.cache
    records.push(...result.records)
  }

  if (enabled.has('kimi')) {
    const files = (await listJsonl(join(cfg.kimiHome, 'sessions')))
      .filter(file => basename(file.path) === 'wire.jsonl')
    const result = await updateFileCache(files, state.files.kimi, content => parseKimi(content))
    state.files.kimi = result.cache
    records.push(...result.records)
  }

  return records
}

async function main() {
  const url = (process.env.OBSERVATORY_URL ?? 'http://localhost:5039').replace(/\/$/, '')
  const apiKey = process.env.OBSERVATORY_API_KEY
  if (!apiKey) { console.error('OBSERVATORY_API_KEY not set; nothing to do.'); process.exit(0) }

  const cfg = {
    codexHome: process.env.CODEX_HOME ?? join(homedir(), '.codex'),
    copilotHome: process.env.COPILOT_HOME ?? join(homedir(), '.copilot'),
    claudeHome: process.env.CLAUDE_HOME ?? join(homedir(), '.claude'),
    kimiHome: process.env.KIMI_HOME ?? join(homedir(), '.kimi-code'),
  }
  const statePath = process.env.OBSERVATORY_STATE ?? join(homedir(), '.ai-observatory', 'sweep-state.json')
  const state = await loadState(statePath)
  delete state.emitted
  const enabled = parseLocalSources(process.env.OBSERVATORY_LOCAL_SOURCES)
  const inventory = await fetchSnapshotInventory(url, apiKey)
  const snapshots = buildDailySnapshots(await scanRecords(cfg, state, enabled))
  const submissions = planSnapshotSubmissions(snapshots, inventory)
  await saveState(statePath, state)

  let posted = 0
  for (const submission of submissions) {
    if (await postEvent(url, apiKey, { ...submission.snapshot, observedAtUtc: new Date().toISOString() })) {
      posted++
    }
  }

  log(`Sweep complete: ${posted} event(s) ${DRY_RUN ? 'would be ' : ''}posted.`)
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  main().catch(error => { console.error(error); process.exit(1) })
}
