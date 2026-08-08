# Scheduled ingest and intelligence jobs

**Status:** approved 2026-08-08
**Goal:** run both daily background workers as scheduled GitHub Actions jobs instead of
resident App Service background services, so `fpaiobs-api-plan` can drop from B1 to F1 Free.

## Why

An Azure cost sweep on 2026-08-08 put `fpaiobs-rg` at roughly GBP 37/month, of which the
App Service plan is GBP 9.90/30d. The plan is B1 for exactly one reason: two
`BackgroundService` loops need the app resident, and **Always On is not offered below
Basic** (Microsoft's App Service limits table: the Always On row is blank for Free and
Shared). Removing both loops removes the only thing pinning the plan above Free.

The prize is small and stated plainly: about GBP 120/year for roughly a day's work. It was
approved on the understanding that the secondary benefits carry equal weight — each job
becomes independently runnable, a failure surfaces as a red workflow instead of silence,
and two processes stop being paid for to sleep 23 hours a day.

## Current state

Observed on 2026-08-08, not assumed:

- `AiObservatory.Ingest` — `ProviderPollingWorkerService : BackgroundService`, hourly loop,
  each cycle ingesting a trailing `LookbackDays` window ending **yesterday**. It hosts a
  minimal Kestrel serving only `/healthz`, which exists solely to answer Linux App Service's
  startup probe (see the restart-loop trap in `deploy-and-ci-traps.md`).
- `AiObservatory.Api` — `IntelligenceWorkerService : BackgroundService`, which computes the
  next midnight UTC and sleeps until it, so it is **already daily**. On start it runs
  `RunAnalysisCatchupAsync`, capped at 7 days, so it already tolerates missed runs.
- Both apps share `fpaiobs-api-plan` (B1). The plan SKU is per plan, so one resident worker
  pins it regardless of the other app.
- `fpaiobs-db` firewall is the union of both apps' `possibleOutboundIpAddresses`
  (`infra/main.bicep`). There is no blanket allow-Azure-services rule.
- CPU headroom against F1's 60 min/day ceiling: `fpaiobs-api` used 12.7-17.8 min/day and
  `fpaiobs-ingest` 7-12.4 min/day over Aug 1-7, and the API's figure includes the
  intelligence worker this change removes.

Neither worker needs to be resident. Both are daily-cadence and both self-heal from a missed
run — ingest through its trailing lookback window, intelligence through its 7-day catch-up.

## Approach

Each app gains a run-once switch that reuses **its own existing composition root verbatim**.
No services move between projects.

- `AiObservatory.Ingest --once` — build the host without the Kestrel web host and without
  `AddHostedService`, resolve the worker, run one poll cycle, exit. The startup-probe Kestrel
  hack is deleted along with the resident loop.
- `AiObservatory.Api --run-jobs` — skip `app.Run()`, resolve `IntelligenceWorkerService`, run
  one pass, exit. Every service it needs is already registered.

`AddHostedService` is **removed from both apps**, not made conditional. Left in place, the
deployed API would keep running its loop resident on F1 with no signal that it was happening,
which is the failure this change exists to remove. One code path; one place the work runs.

### Rejected alternatives

- **Azure Container Apps job on a cron.** Needs a new ACA environment and a registry (GHCR,
  or ACR at GBP 3.80/month — 38% of the prize). Consumption egress IPs are not stable, so the
  Postgres allowlist would need a NAT gateway (about GBP 25/month, making the saving negative)
  or a blanket Azure-services rule, which is a security downgrade from the current per-IP
  allowlist.
- **Azure Functions on Consumption with timer triggers.** Executions are free at this volume,
  but it means porting two substantial DI composition roots onto the Functions host, and the
  egress IPs are equally dynamic, so it hits the identical firewall problem.
- **An HTTP trigger endpoint on the ingest app.** Forbidden by existing design intent:
  `AiObservatory.Ingest/Program.cs` states the worker must never grow an API surface, because
  it holds provider credentials the public-facing API does not.

Every Azure-hosted replacement founders on the same rock — unstable egress against an
IP-allowlisted database — and fixing it costs more than the saving. A GitHub runner can open
and close its own hole, which is why approach A wins.

## Components

### `ProviderPollingWorkerService`

Lift the body of `ExecuteAsync`'s `while` loop into:

```csharp
public async Task<PollOutcome> RunOnceAsync(CancellationToken ct)
```

`ExecuteAsync` becomes a thin wrapper for local development. The existing per-arm `try/catch`
blocks stay exactly as they are — each arm still isolated — but each arm's result is now
recorded in the returned `PollOutcome` rather than only logged.

### `IntelligenceWorkerService`

The same refactor: `RunOnceAsync(CancellationToken)` containing the current
`RunAnalysisCatchupAsync` / `RunBudgetCheckAsync` / `RunGitHubBillingSyncAsync` sequence,
returning an outcome. The midnight-delay arithmetic belongs to the wrapper and is not used by
the job.

### `PollOutcome`

One record per run: which arms were configured, which succeeded, which failed and with what
exception. It is what the entrypoint turns into an exit code, and what the tests assert on.

### Entrypoints

Both `Program.cs` files branch on the CLI argument **before** building. The no-argument path
must remain the existing web behaviour unchanged, because
`WebApplicationFactory<Program>` constructs the host with no args and every composition-root
test depends on it.

## Scheduling and data flow

**One workflow, not two.** Intelligence analyses what ingest has just written, so the two are
ordered, not independent — sequential steps in a single scheduled workflow, ingest first.
Today they are only loosely coupled because ingest runs hourly and intelligence at midnight;
making the dependency explicit is strictly better than inheriting that accident.

Cadence: once daily. Confirmed with the owner — every provider call ingests yesterday's
usage, so an hourly poll bought nothing.

```
schedule (daily, after 00:00 UTC so "yesterday" is complete)
  -> azure/login (OIDC, existing federated identity)
  -> prune leftover gha-* firewall rules
  -> resolve runner egress IP, create firewall rule gha-<run_id>
  -> read secrets from Key Vault, mask, export as env vars
  -> dotnet AiObservatory.Ingest.dll --once
  -> dotnet AiObservatory.Api.dll --run-jobs
  -> delete firewall rule            [if: always()]
```

## Error handling

**The exit code is the point of this change.** Both workers catch broadly by design, so one
bad arm cannot kill a loop that must survive to tomorrow. Transplanted unchanged into a
scheduled job, that same code produces a green workflow with no data written — the exact
silent failure the ingest app was rebuilt to eliminate.

Decided: **exit non-zero if any configured arm failed.** Not "all arms failed", which is
today's GitHub-repo rule and far too weak for a job. A provider outage will red the workflow
until it clears; that is information, not an incident, because the trailing lookback window
repairs the gap on the next successful run.

An arm that is not configured is not a failure. The existing "NOT CONFIGURED" logging stays,
because an unregistered arm and a registered arm that found nothing are otherwise
indistinguishable.

## Security

- Secrets are read from Key Vault at run time through the existing OIDC identity and exported
  under the names `Program.cs` already reads (`DB_CONNECTION`, `ANTHROPIC_BILLING_KEY`,
  `GITHUB_TOKEN`, `COPILOT_ORG`, `Ingest__GitHubRepoAllowlist`, `GOOGLE_BILLING_ACCOUNT_ID`).
  Nothing is duplicated into GitHub secrets. Each is masked with `::add-mask::` on read.
- The GitHub federated identity needs **Key Vault Secrets User** on `fpaiobs-kv`, and
  permission to manage firewall rules on `fpaiobs-db`.
- **This repository is public.** The workflow carries `schedule` and `workflow_dispatch`
  triggers only — never `pull_request` or `pull_request_target`, either of which would expose
  live provider credentials to code from a fork — with `permissions: { id-token: write,
  contents: read }` and nothing else.
- Net firewall position improves: two App Service outbound ranges currently sit on the
  allowlist permanently; afterwards a single address is allowed for the couple of minutes a
  run takes. Rules are pruned at the start of each run as well as deleted at the end, because
  a cancelled run's cleanup cannot be assumed to have executed.

## Infrastructure changes

- Delete `fpaiobs-ingest` from `infra/modules/ingest.bicep` and its `main.bicep` wiring,
  including its Key Vault role assignment and its contribution to the Postgres allowlist.
- `fpaiobs-api-plan`: `B1` -> `F1`; `alwaysOn: true` -> `false` on `fpaiobs-api`.
- Add the Key Vault role assignment for the GitHub federated identity.

Accepted consequence: the API cold-starts after 20 minutes idle. Confirmed acceptable — this
is a single-user dashboard.

## Testing

- `RunOnceAsync` returns a failed outcome when an arm throws, and a successful one when every
  configured arm succeeds.
- The entrypoint maps a failed outcome to a non-zero exit code and a successful one to zero.
- An unconfigured arm does not produce a failed outcome.
- Existing composition-root tests must pass untouched, which the no-args-is-web-mode rule
  above guarantees.

## Out of scope

- **`GOOGLE_APPLICATION_CREDENTIALS` is never set by `ingest.bicep`**, although
  `GOOGLE_BILLING_ACCOUNT_ID` is. `GoogleBillingClient` needs the credentials file, so if that
  Key Vault secret is populated the Google arm registers and then fails authentication on
  every cycle. Whether the secret exists has not been checked. This change inherits the
  behaviour unaltered; it is recorded here so the migration does not get blamed for it, and
  it should be settled separately.
- Reducing App Insights ingestion further. The `AppTraces` trim landed on 2026-08-08 in
  `9a8bce9` and `f1d0f2a`; its effect had not yet reached billing data when this was written.

## Rollback

The Bicep change is revertible, but `fpaiobs-ingest` is deleted rather than stopped, so
reverting means redeploying the app, not restarting it. The workflow can be disabled
independently of the infrastructure if the jobs misbehave while the plan change is fine.
