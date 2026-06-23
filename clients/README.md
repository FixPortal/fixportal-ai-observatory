# Local usage producers

Some AI coding CLIs cannot report their own usage to the Observatory:

- **Codex** and **Copilot** running on **subscription/enterprise seats** are
  invisible to the vendor billing APIs the `AiObservatory.Ingest` worker polls
  (the OpenAI platform usage API only sees metered API keys; the GitHub Copilot
  org-metrics API is admin-only and reports engagement, not tokens/cost).

Both tools, however, write complete per-session token data to disk. The
**`observatory-sweep.mjs`** producer reads those local logs and pushes the
per-session token deltas to the `POST /api/events` endpoint, so subscription
usage still shows up on the dashboard. Costs are computed locally and are
**notional** for subscription-billed tools (useful for relative comparison, not
an invoice).

| Tool | Log read | Provider recorded |
|---|---|---|
| Codex | `~/.codex/sessions/YYYY/MM/DD/rollout-*.jsonl` (last `token_count`) | `OpenAI` |
| Copilot | `~/.copilot/session-state/<sid>/events.jsonl` (last `session.shutdown`) | `Copilot` |

It no-ops cleanly for whichever tool isn't installed, so you can run it whether
you use one or both.

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

Re-running is always safe: per-session cumulative state plus a server-side
idempotency `eventKey` mean retries never double-count. The first run backfills
every session within the window; later runs only post the growth since.

## Environment variables

| Variable | Default | Purpose |
|---|---|---|
| `OBSERVATORY_API_KEY` | *(required)* | Sent as the `X-Observatory-Key` header. Without it the script exits cleanly doing nothing. |
| `OBSERVATORY_URL` | `http://localhost:5000` | Base URL of the Observatory API. |
| `OBSERVATORY_STATE` | `~/.ai-observatory/sweep-state.json` | Where cumulative per-session state is kept. |
| `OBSERVATORY_WINDOW_DAYS` | `30` | Only consider session logs modified within this many days. |
| `CODEX_HOME` | `~/.codex` | Override the Codex home (e.g. a non-standard install). |
| `COPILOT_HOME` | `~/.copilot` | Override the Copilot home. |

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
(`parseCodex`, `parseCopilot`). To cover a new CLI: add a `parseX` that returns
cumulative `{ input, output, cacheRead, cacheWrite }` per model, a pricing row,
and a `sweepX` arm that walks its log dir — following the existing two. The
provider string must be one of the API's `Provider` enum values
(`Anthropic`, `Copilot`, `Google`, `OpenAI`).
