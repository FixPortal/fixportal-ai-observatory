#!/usr/bin/env node
// AI Observatory local usage sweeper (drop-in).
//
// Sweeps on-disk session logs for AI coding CLIs that do not (or cannot) push
// usage themselves, and POSTs the per-session token deltas to the Observatory
// `/api/events` endpoint. Today it covers:
//
//   Codex    ~/.codex/sessions/YYYY/MM/DD/rollout-*.jsonl
//            last `token_count` event -> cumulative total_token_usage
//   Copilot  ~/.copilot/session-state/<sid>/events.jsonl
//            last `session.shutdown` event -> data.modelMetrics.<model>.usage
//
// Neither tool's *billing* API can see subscription-seat usage (ChatGPT/Codex
// enterprise seats and Copilot seats are invisible to the OpenAI platform usage
// API and the GitHub org metrics API respectively), but both leave complete
// token data on disk. Costs are therefore computed locally and are NOTIONAL for
// subscription-billed tools -- useful for relative comparison, not an invoice.
//
// Zero dependencies: Node 18+ only (global fetch, fs/promises). Re-running never
// double-counts -- per-session cumulative state + a server-side idempotency
// `eventKey` make every post safe to retry.
//
// Usage:
//   OBSERVATORY_API_KEY=... node observatory-sweep.mjs [--dry-run] [--verbose]
// See clients/README.md for env vars and scheduling.

import { readFile, readdir, mkdir, writeFile, stat } from 'node:fs/promises'
import { homedir } from 'node:os'
import { join, dirname, basename } from 'node:path'
import { pathToFileURL } from 'node:url'

// --- Pricing (USD per million tokens): [input, output, cacheRead, cacheWrite] ---
// Subscription-billed tools have no real per-token price; these are notional and
// kept only so relative spend is comparable. Matched by longest-prefix StartsWith.
const OPENAI_PRICING = {
  'gpt-5.5':        [1.25, 10.00, 0.125, 0],
  'gpt-5.4':        [1.25, 10.00, 0.125, 0],
  'gpt-5':          [1.25, 10.00, 0.125, 0],
  'o3':             [10.00, 40.00, 2.50, 0],
  'o4-mini':        [1.10, 4.40, 0.275, 0],
  'gpt-4.1-mini':   [0.40, 1.60, 0.10, 0],
  'gpt-4.1':        [2.00, 8.00, 0.50, 0],
  'gpt-4o-mini':    [0.15, 0.60, 0.075, 0],
  'gpt-4o':         [2.50, 10.00, 1.25, 0],
}
const OPENAI_DEFAULT = [1.25, 10.00, 0.125, 0] // gpt-5 basis

const COPILOT_PRICING = {
  'gpt-5.4':        [1.25, 10.00, 0.125, 0],
  'gpt-5':          [1.25, 10.00, 0.125, 0],
  'gpt-4.1':        [2.00, 8.00, 0.50, 0],
  'gpt-4o':         [2.50, 10.00, 1.25, 0],
  'claude-opus-4':  [15.00, 75.00, 1.50, 3.75],
  'claude-sonnet-4':[3.00, 15.00, 0.30, 0.75],
  'claude-haiku-4': [0.80, 4.00, 0.08, 0.20],
}
const COPILOT_DEFAULT = [2.00, 8.00, 0.50, 0] // gpt-4.1 basis

// --- Pure helpers (unit-tested in observatory-sweep.test.mjs) -----------------

/** Longest-prefix model match so "gpt-4o-mini-2024" resolves to "gpt-4o-mini", not "gpt-4o". */
export function pickRates(table, model, fallback) {
  const m = (model ?? '').toLowerCase()
  let best = null
  for (const key of Object.keys(table)) {
    if (m.startsWith(key) && (best === null || key.length > best.length)) { best = key }
  }
  return best ? table[best] : fallback
}

/** cost in USD from a token delta and a [in,out,cacheRead,cacheWrite] rate row. */
export function costUsd(rates, d) {
  const c = (d.input * rates[0] + d.output * rates[1] + d.cacheRead * rates[2] + d.cacheWrite * rates[3]) / 1e6
  return Math.round(c * 1e8) / 1e8
}

/**
 * Delta between the cumulative totals already posted (prev) and the current
 * cumulative (cum). If anything went backwards (log rotated / session reset),
 * treat the current totals as the delta rather than emitting a negative.
 */
export function deltaTokens(prev, cum) {
  const p = prev ?? { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 }
  let d = {
    input: cum.input - (p.input ?? 0),
    output: cum.output - (p.output ?? 0),
    cacheRead: cum.cacheRead - (p.cacheRead ?? 0),
    cacheWrite: cum.cacheWrite - (p.cacheWrite ?? 0),
  }
  if (d.input < 0 || d.output < 0 || d.cacheRead < 0 || d.cacheWrite < 0) { d = { ...cum } }
  return d
}

/**
 * Parse a Codex rollout .jsonl into { model, cum, endedAt } or null.
 * The cumulative session totals live in the LAST `token_count` event's
 * total_token_usage; input_tokens there already includes cached_input_tokens,
 * so we split them out. Output already includes reasoning tokens (OpenAI bills
 * reasoning as output), so we leave it whole and carry reasoning in rawPayload.
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
    let o
    try { o = JSON.parse(line) } catch { continue }
    const p = o.payload
    if (o.type === 'turn_context' && p?.model) { model = p.model }
    if (p?.type === 'token_count' && p.info?.total_token_usage) {
      total = p.info.total_token_usage
      reasoning = total.reasoning_output_tokens ?? 0
      if (o.timestamp) { endedAt = o.timestamp }
    }
  }
  if (!total) { return null }
  const cacheRead = Math.max(0, total.cached_input_tokens ?? 0)
  const cum = {
    input: Math.max(0, (total.input_tokens ?? 0) - cacheRead),
    output: Math.max(0, total.output_tokens ?? 0),
    cacheRead,
    cacheWrite: 0,
  }
  return { model: model ?? 'gpt-5', cum, reasoning, endedAt }
}

/**
 * Parse a Copilot events.jsonl into { endedAt, perModel } or null.
 * The last `session.shutdown` event carries cumulative per-model usage. Its
 * usage.inputTokens includes cache reads, so we split them out to avoid
 * double-counting input + cacheRead.
 */
export function parseCopilot(content) {
  let shutdown = null
  for (const line of content.split('\n')) {
    if (!line || !line.includes('"session.shutdown"')) { continue }
    try { shutdown = JSON.parse(line) } catch { /* keep last parseable */ }
  }
  const metrics = shutdown?.data?.modelMetrics
  if (!metrics) { return null }
  const perModel = {}
  for (const [model, v] of Object.entries(metrics)) {
    const u = v?.usage
    if (!u) { continue }
    const cacheRead = Math.max(0, u.cacheReadTokens ?? 0)
    perModel[model] = {
      input: Math.max(0, (u.inputTokens ?? 0) - cacheRead),
      output: Math.max(0, u.outputTokens ?? 0),
      cacheRead,
      cacheWrite: Math.max(0, u.cacheWriteTokens ?? 0),
      reasoning: u.reasoningTokens ?? 0,
    }
  }
  return { endedAt: shutdown.timestamp ?? null, perModel }
}

const SID_RE = /([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\.jsonl$/i
/** Codex session id is the trailing UUID of the rollout filename. */
export function codexSidFromPath(path) {
  const m = basename(path).match(SID_RE)
  return m ? m[1] : basename(path).replace(/\.jsonl$/i, '')
}

// --- IO / orchestration (below the test boundary) ----------------------------

const VERBOSE = process.argv.includes('--verbose')
const DRY_RUN = process.argv.includes('--dry-run')
const log = (...a) => { if (VERBOSE) { console.error(...a) } }

async function loadState(path) {
  try { return JSON.parse(await readFile(path, 'utf8')) }
  catch { return { codex: {}, copilot: {} } }
}

async function saveState(path, state) {
  if (DRY_RUN) { return }
  await mkdir(dirname(path), { recursive: true })
  await writeFile(path, JSON.stringify(state, null, 2), 'utf8')
}

async function postEvent(url, apiKey, body) {
  if (DRY_RUN) { log('DRYRUN would post:', JSON.stringify(body)); return true }
  try {
    const res = await fetch(`${url}/api/events`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-Observatory-Key': apiKey },
      body: JSON.stringify(body),
      signal: AbortSignal.timeout(10_000),
    })
    if (!res.ok) { log(`POST ${res.status} for ${body.eventKey}`); return false }
    return true
  } catch (e) { log(`POST failed for ${body.eventKey}:`, e.message); return false }
}

/** Recursively list *.jsonl under dir modified at/after `since` (ms epoch). */
async function listJsonl(dir, since, out = []) {
  let entries
  try { entries = await readdir(dir, { withFileTypes: true }) } catch { return out }
  for (const e of entries) {
    const full = join(dir, e.name)
    if (e.isDirectory()) { await listJsonl(full, since, out) }
    else if (e.name.endsWith('.jsonl')) {
      const s = await stat(full)
      if (s.mtimeMs >= since) { out.push({ path: full, mtimeMs: s.mtimeMs, mtime: s.mtime }) }
    }
  }
  return out
}

async function sweepCodex(cfg, state, ctx) {
  const root = join(cfg.codexHome, 'sessions')
  const files = await listJsonl(root, cfg.windowStart)
  for (const f of files) {
    const sid = codexSidFromPath(f.path)
    const mtKey = `mt:${sid}`
    if (state.codex[mtKey] === f.mtimeMs) { continue } // unchanged since last sweep

    const parsed = parseCodex(await readFile(f.path, 'utf8'))
    if (!parsed) { state.codex[mtKey] = f.mtimeMs; continue }

    const d = deltaTokens(state.codex[sid], parsed.cum)
    if (d.input === 0 && d.output === 0 && d.cacheRead === 0) {
      state.codex[sid] = parsed.cum; state.codex[mtKey] = f.mtimeMs; continue
    }
    const cost = costUsd(pickRates(OPENAI_PRICING, parsed.model, OPENAI_DEFAULT), d)
    const body = {
      provider: 'OpenAI',
      model: parsed.model,
      inputTokens: d.input,
      outputTokens: d.output,
      cacheReadTokens: d.cacheRead,
      cacheWriteTokens: d.cacheWrite,
      costUsd: cost,
      eventKey: `codex:${sid}:${parsed.model}:${parsed.cum.input}-${parsed.cum.output}`,
      occurredAtUtc: parsed.endedAt ?? f.mtime.toISOString(),
      rawPayload: JSON.stringify({ source: 'observatory-sweep', tool: 'codex', session_id: sid, reasoning_tokens: parsed.reasoning, note: 'costUsd is notional - Codex is subscription-billed' }),
    }
    if (await ctx.post(body)) {
      state.codex[sid] = parsed.cum
      state.codex[mtKey] = f.mtimeMs
      ctx.posted++
      await ctx.save()
    }
  }
}

async function sweepCopilot(cfg, state, ctx) {
  const root = join(cfg.copilotHome, 'session-state')
  let dirs
  try { dirs = await readdir(root, { withFileTypes: true }) } catch { return }
  for (const dir of dirs) {
    if (!dir.isDirectory()) { continue }
    const sid = dir.name
    const eventsFile = join(root, sid, 'events.jsonl')
    let s
    try { s = await stat(eventsFile) } catch { continue }
    if (s.mtimeMs < cfg.windowStart) { continue }
    const mtKey = `mt:${sid}`
    if (state.copilot[mtKey] === s.mtimeMs) { continue }

    const parsed = parseCopilot(await readFile(eventsFile, 'utf8'))
    if (!parsed) { continue } // no shutdown yet -> session likely still running; revisit next sweep

    let allOk = true
    for (const [model, cum] of Object.entries(parsed.perModel)) {
      if (cum.input === 0 && cum.output === 0) { continue }
      const sessKey = `${sid}|${model}`
      const d = deltaTokens(state.copilot[sessKey], cum)
      if (d.input === 0 && d.output === 0 && d.cacheRead === 0 && d.cacheWrite === 0) {
        state.copilot[sessKey] = cum; continue
      }
      const cost = costUsd(pickRates(COPILOT_PRICING, model, COPILOT_DEFAULT), d)
      const body = {
        provider: 'Copilot',
        model,
        inputTokens: d.input,
        outputTokens: d.output,
        cacheReadTokens: d.cacheRead,
        cacheWriteTokens: d.cacheWrite,
        costUsd: cost,
        eventKey: `copilot:${sid}:${model}:${cum.input}-${cum.output}`,
        occurredAtUtc: parsed.endedAt ?? s.mtime.toISOString(),
        rawPayload: JSON.stringify({ source: 'observatory-sweep', tool: 'copilot', session_id: sid, reasoning_tokens: cum.reasoning, note: 'costUsd is notional - Copilot is subscription-billed' }),
      }
      if (await ctx.post(body)) { state.copilot[sessKey] = cum; ctx.posted++; await ctx.save() }
      else { allOk = false }
    }
    if (allOk) { state.copilot[mtKey] = s.mtimeMs; await ctx.save() }
  }
}

async function main() {
  const url = (process.env.OBSERVATORY_URL ?? 'http://localhost:5000').replace(/\/$/, '')
  const apiKey = process.env.OBSERVATORY_API_KEY
  if (!apiKey) { console.error('OBSERVATORY_API_KEY not set; nothing to do.'); process.exit(0) }

  const windowDays = Number(process.env.OBSERVATORY_WINDOW_DAYS ?? 30)
  const cfg = {
    codexHome: process.env.CODEX_HOME ?? join(homedir(), '.codex'),
    copilotHome: process.env.COPILOT_HOME ?? join(homedir(), '.copilot'),
    windowStart: Date.now() - windowDays * 86_400_000,
  }
  const statePath = process.env.OBSERVATORY_STATE ?? join(homedir(), '.ai-observatory', 'sweep-state.json')
  const state = await loadState(statePath)
  state.codex ??= {}
  state.copilot ??= {}

  const ctx = {
    posted: 0,
    post: (body) => postEvent(url, apiKey, body),
    save: () => saveState(statePath, state),
  }

  await sweepCodex(cfg, state, ctx)
  await sweepCopilot(cfg, state, ctx)
  await saveState(statePath, state)

  log(`Sweep complete: ${ctx.posted} event(s) ${DRY_RUN ? 'would be ' : ''}posted.`)
}

// Run main only when invoked directly, so the pure helpers stay importable.
if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  main().catch((e) => { console.error(e); process.exit(1) })
}
