# Local usage producers

Some AI coding CLIs cannot report their own subscription usage to the Observatory:

- **Codex**, **Copilot**, **Claude**, and **Kimi** running on subscription seats are
  invisible to the vendor billing APIs the `AiObservatory.Ingest` worker polls
  (or require an organisation plan unavailable to personal subscriptions).

The **`observatory-sweep.mjs`** producer reads their local telemetry and sends
cumulative daily/model snapshots to `POST /api/events`. Stable source-scoped keys
let a changed transcript correct the same server row without aggregate drift.
Codex and Copilot retain their local **notional** comparison; Claude is priced by
the API's Anthropic transition table; Kimi cost remains unknown.

| Tool | Log read | Provider recorded |
|---|---|---|
| Codex | `~/.codex/sessions/YYYY/MM/DD/rollout-*.jsonl` (last `token_count`) | `OpenAI` |
| Copilot | `~/.copilot/session-state/<sid>/events.jsonl` (last `session.shutdown`) | `Copilot` |
| Claude | `~/.claude/projects/**/*.jsonl` (`assistant` messages with usage, deduplicated by `message.id`) | `Anthropic` |
| Kimi | `~/.kimi-code/sessions/**/wire.jsonl` (`usage.record` only; `step.end` mirrors are ignored) | `Moonshot` |

It no-ops cleanly for whichever tools are not installed.

## Requirements

- **Node 18+** (uses the built-in `fetch` and `fs/promises`). No `npm install` —
  zero dependencies. Node is already present if you run Codex or Copilot CLI.

## Run

```bash
OBSERVATORY_URL=https://your-observatory.example \
OBSERVATORY_API_KEY=your-key \
node clients/observatory-sweep.mjs
```

Preview without posting (and see exactly what would be sent):

```bash
OBSERVATORY_API_KEY=your-key node clients/observatory-sweep.mjs --dry-run --verbose
```

Re-running is always safe. Every transcript remains part of the cumulative truth;
the state file caches parsed records by path and mtime to avoid rereading unchanged
logs and remembers emitted keys so removed observations can be corrected to zero.
It is not the system of record. Deleting it only causes a full rescan and harmless
resubmission under the same stable keys.

## Environment variables

| Variable | Default | Purpose |
|---|---|---|
| `OBSERVATORY_API_KEY` | *(required)* | Sent as the `X-Observatory-Key` header. Without it the script exits cleanly doing nothing. |
| `OBSERVATORY_URL` | `http://localhost:5039` | Base URL of the Observatory API. The default is the API's `dotnet run` address. Under Compose the API publishes no host port — use `http://localhost:4173`, the frontend's nginx proxy. Against a deployment, set the full origin. |
| `OBSERVATORY_STATE` | `~/.ai-observatory/sweep-state.json` | Per-file parse and emitted-key cache. Safe to delete. |
| `OBSERVATORY_LOCAL_SOURCES` | `codex,copilot,claude,kimi` | Comma-separated collector allowlist. Exclude `claude` when Claude Code Usage API coverage overlaps it. |
| `CODEX_HOME` | `~/.codex` | Override the Codex home (e.g. a non-standard install). |
| `COPILOT_HOME` | `~/.copilot` | Override the Copilot home. |
| `CLAUDE_HOME` | `~/.claude` | Override the Claude home. |
| `KIMI_HOME` | `~/.kimi-code` | Override the Kimi home. |

## Schedule it

The sweep is throttle-free and idempotent, so just run it on a timer.

**macOS / Linux (cron, every 15 minutes):**

```bash
crontab -e
# add (adjust the path and key):
*/15 * * * * OBSERVATORY_URL=https://your-observatory.example OBSERVATORY_API_KEY=your-key /usr/bin/node /path/to/clients/observatory-sweep.mjs >/dev/null 2>&1
```

**Windows (Task Scheduler, every 15 minutes):**

```powershell
$env:OBSERVATORY_URL = 'https://your-observatory.example'
$action  = New-ScheduledTaskAction -Execute 'node' -Argument "$HOME\path\to\clients\observatory-sweep.mjs"
$trigger = New-ScheduledTaskTrigger -Once -At (Get-Date) -RepetitionInterval (New-TimeSpan -Minutes 15)
Register-ScheduledTask -TaskName 'AiObservatorySweep' -Action $action -Trigger $trigger
```

Set `OBSERVATORY_API_KEY` as a user environment variable (`setx OBSERVATORY_API_KEY your-key`)
so the scheduled task picks it up.

## Test

```bash
node --test clients/observatory-sweep.test.mjs
```

## Adding another tool

The parsers are small pure functions in `observatory-sweep.mjs`
(`parseCodex`, `parseCopilot`, `parseClaude`, `parseKimi`). To cover a new CLI,
add its normalized records to `buildDailySnapshots` and scan its log directory.
The provider string must be one of the API's `Provider` enum values
(`Anthropic`, `Copilot`, `Google`, `OpenAI`, `Moonshot`).
