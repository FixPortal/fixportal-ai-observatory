# Local usage producers

> Machine-local Codex, Copilot, Claude, and Kimi telemetry guide as of 2026-08-25. These are best-effort subscription/notional facts, not invoices.

`observatory-sweep.mjs` reads installed CLI logs and posts cumulative daily/model snapshots to `POST /api/events`. It has no dependencies beyond Node 18+.

Each posted snapshot carries explicit provenance so the API preserves its subscription/notional meaning:

```json
{
  "provider": "OpenAI",
  "model": "codex",
  "inputTokens": 1200,
  "outputTokens": 400,
  "cacheReadTokens": 150,
  "cacheWriteTokens": 80,
  "cacheWrite1hTokens": 0,
  "thoughtTokens": 120,
  "costUsd": null,
  "eventKey": "codex:2026-08-25:codex",
  "occurredAtUtc": "2026-08-25T10:00:00.000Z",
  "sourceId": "codex-local",
  "sourceKind": "localTelemetry",
  "usageScope": "subscription",
  "costBasis": "notional",
  "observedAtUtc": "2026-08-25T10:00:00Z",
  "runtime": "codex",
  "rawPayload": "{\"source\":\"observatory-sweep\",\"tool\":\"codex\",\"thinking_tokens\":120,\"processing\":\"standard\",\"context\":\"short\",\"region\":\"global\"}"
}
```

## What it reads

| Tool | Local path and fact | Meaning and safeguard |
| --- | --- | --- |
| Codex | `~/.codex/sessions/**/rollout-*.jsonl`; final cumulative `token_count` | `codex-local` / subscription / notional; final cumulative value wins |
| Copilot | `~/.copilot/session-state/**/events.jsonl`; final `session.shutdown` per-model totals | `copilot-local` / subscription / notional; final cumulative totals win |
| Claude | `~/.claude/projects/**/*.jsonl`; assistant usage | `claude-local` / subscription / notional; global `message.id` dedupe retains the richest copy |
| Kimi | `~/.kimi-code/sessions/**/wire.jsonl`; `usage.record` only | `kimi-local` / subscription / notional; mirrored `step.end` rows do not count; turn and session scopes count |

Stable source-scoped keys make resubmission safe. Before posting, the sweeper reads server inventory and emits zero corrections for removed or disabled snapshots. Its state file is only a parse cache; losing it causes a safe full rescan, not a loss of server truth.

> [!WARNING]
> Set `OBSERVATORY_LOCAL_SOURCES` without `claude` when `claude-code-usage-api` covers the same account/activity. The two lanes do not cross-deduplicate.

## Run

Set `OBSERVATORY_API_KEY` to `<observatory-api-key>` and, when needed, `OBSERVATORY_URL` to the Observatory API origin, then run:

```powershell
node clients/observatory-sweep.mjs
```

Preview without posting:

```powershell
node clients/observatory-sweep.mjs --dry-run --verbose
```

## Environment

| Variable | Default | Purpose |
| --- | --- | --- |
| `OBSERVATORY_API_KEY` | Required | Sent as `X-Observatory-Key`; absent means no post. |
| `OBSERVATORY_URL` | `http://localhost:5039` | API origin; use `http://localhost:4173` for Compose's frontend proxy. |
| `OBSERVATORY_STATE` | `~/.ai-observatory/sweep-state.json` | Safe-to-delete parse cache. |
| `OBSERVATORY_LOCAL_SOURCES` | `codex,copilot,claude,kimi` | Comma-separated collector allowlist. |
| `CODEX_HOME`, `COPILOT_HOME`, `CLAUDE_HOME`, `KIMI_HOME` | Tool homes above | Optional home overrides. |

Schedule the same one-line command with your platform's scheduler. Re-running is safe and idempotent; there is no throttle or separate server-side sweeper state to maintain.

## Schedule

Run the sweep every 15 minutes with cron, Task Scheduler, or the scheduler already used for your developer machine. Give the scheduled process the same `OBSERVATORY_API_KEY`, optional `OBSERVATORY_URL`, and home overrides; preview with `--dry-run --verbose` before enabling posts.

## Test

```powershell
node --test clients/observatory-sweep.test.mjs
```
