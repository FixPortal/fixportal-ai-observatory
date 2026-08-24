# Task 4 report — Poll registered sources and persist freshness

Commit: final SHA reported in the parent handoff because this report is part of that commit.

## Implementation summary

- Added the minimum `IUsageSource` range contract, `SourceIngestionResult`, `SourceDefinition`, and explicit `SourceUnavailableException`.
- Added `SourceSyncState` and a concrete scoped store. State records configuration, nullable availability, expected refresh interval, attempt/success/latest observation instants, consecutive failures, and a sanitized 500-character error.
- Generated the EF migration with its actual timestamp, `20260824195918_AddSourceSyncStates`, and updated the model snapshot. No timestamp was renamed to the illustrative plan value.
- Replaced the concrete-provider polling list with one scoped enumeration of registered source definitions and implementations. Unconfigured or missing implementations are persisted as unconfigured; configured sources run independently; cancellation propagates; persisted failure counts retain the three-failure escalation.
- Sanitized persisted/logged errors by removing CR/LF and HTTP(S) query strings before the 500-character bound. The generic worker neither names providers nor logs the raw exception.
- Adapted Anthropic, Copilot, Google, OpenAI, and GitHub to the range contract. Daily APIs iterate the inclusive range; GitHub consumes `from` once. Each returns its greatest upstream observation instant, or null when the upstream shape exposes none.
- Moved GitHub's all-configured-repositories-failed decision into `GitHubIngestionService.IngestAsync`. A total outage now fails the source, rate exhaustion is unavailable, and the generic worker has no GitHub-specific branch.
- Registered exactly one definition per known source and credential-gated `IUsageSource` implementations with `TryAddEnumerable`. GitHub activity uses the approved distinct stable identity `github-activity-api`, separate from `github-billing-api`.
- After a successful local-telemetry POST, `/api/events` refreshes source state using the injected current `Instant`, a one-day expected interval, and the request observation instant for Created, Corrected, and Unchanged results.
- Kept the implementation Ponytail-small: one contract, one entity/store, the existing adapters, and the generic worker; no registry, factory, provider dependency, or pricing work was added.

## TDD evidence

| Cycle | Expected RED observed | GREEN evidence |
| --- | --- | --- |
| Contract/state | Focused ingest compilation failed because `IUsageSource`, `SourceDefinition`, and `SourceSyncState` did not exist. | Contract, entity/store, and focused worker tests compiled. |
| Persistent worker | Focused worker tests reached EF's expected `PendingModelChangesWarning` before a migration existed. | Generated migration; focused worker/host tests passed 24/24 against real PostgreSQL. |
| Adapters | Adapter-focused compilation failed because the five services lacked the three-argument range `IngestAsync`. | Full ingest project passed 121/121, including inclusive ranges, latest upstream observations, total GitHub outage, and rate-unavailable behavior. |
| Local POST state | The real-PostgreSQL WAF initially exposed timestamp precision in the Unchanged setup. The test was corrected to use whole-second injected/request instants rather than weakening production comparison. | `EventsEndpointsWafTests` passed 28/28 for Created, Corrected, and Unchanged state refresh. |

Additional focused data verification: `UsageMigrationTests` passed 1/1 against real PostgreSQL.

## Final verification

| Command | Result |
| --- | --- |
| `dotnet test tests/AiObservatory.Ingest.Tests/AiObservatory.Ingest.Tests.csproj --configuration Release --timeout 5m` | Passed 121/121. |
| Focused worker + host filter | Passed 24/24 against real PostgreSQL. |
| Focused `EventsEndpointsWafTests` | Passed 28/28 against real PostgreSQL. |
| Focused `UsageMigrationTests` | Passed 1/1 against real PostgreSQL. |
| `dotnet ef migrations has-pending-model-changes --project src/AiObservatory.Data --startup-project src/AiObservatory.Api` | Passed: no model changes since the migration. |
| `dotnet csharpier check .` | Passed, 180 files checked. |
| `dotnet build AiObservatory.slnx --configuration Release` | Succeeded, 0 errors; one pre-existing `xUnit1025` warning. |
| `dotnet test --solution AiObservatory.slnx --configuration Release --no-build --timeout 5m` | Passed 618/618. |
| `node --test clients/observatory-sweep.test.mjs` | Passed 32/32. |
| `git diff --check` | Passed; Git emitted only the repository line-ending notice for the ingest test project. |
| Generic-worker provider-name scan | No GitHub, Anthropic, OpenAI, Google, or Copilot references found. |

## Files changed

- `.superpowers/sdd/2026-08-24-source-aware-usage-foundation/task-4-report.md`
- `src/AiObservatory.Api/Endpoints/EventsEndpoints.cs`
- `src/AiObservatory.Data/AiObservatoryDbContext.cs`
- `src/AiObservatory.Data/Entities/ObservationProvenance.cs`
- `src/AiObservatory.Data/Entities/SourceSyncState.cs`
- `src/AiObservatory.Data/Migrations/20260824195918_AddSourceSyncStates.cs`
- `src/AiObservatory.Data/Migrations/20260824195918_AddSourceSyncStates.Designer.cs`
- `src/AiObservatory.Data/Migrations/AiObservatoryDbContextModelSnapshot.cs`
- `src/AiObservatory.Data/Repositories/SourceSyncStateStore.cs`
- `src/AiObservatory.Data/ServiceCollectionExtensions.cs`
- `src/AiObservatory.Ingest/Program.cs`
- `src/AiObservatory.Ingest/ProviderPollingWorkerService.cs`
- `src/AiObservatory.Ingest/Services/{Anthropic,Copilot,GitHub,Google,OpenAi}/*IngestionService.cs`
- `src/AiObservatory.Ingest/Sources/IUsageSource.cs`
- `tests/AiObservatory.Api.IntegrationTests/EventsEndpointsWafTests.cs`
- `tests/AiObservatory.Ingest.Tests/AiObservatory.Ingest.Tests.csproj`
- `tests/AiObservatory.Ingest.Tests/IngestHostTests.cs`
- `tests/AiObservatory.Ingest.Tests/Services/{Anthropic,Copilot,GitHub,Google,OpenAi}IngestionServiceTests.cs`
- `tests/AiObservatory.Ingest.Tests/Services/ProviderPollingWorkerServiceTests.cs`

Two additional files are included only because the required repository-wide CSharpier gate found existing formatting drift and the parent explicitly approved the mechanical fixes:

- `src/AiObservatory.Data/Migrations/20260824172007_AddObservationProvenance.cs`
- `tests/AiObservatory.Api.IntegrationTests/SpendEntriesEndpointsWafTests.cs`

For both files, a comparison after removing all whitespace is byte-for-byte equal to `HEAD`; there is no behavior change.

## Self-review

- Traced every current ingestion service, its client/repository dependencies, the previous worker callers, host registrations, and tests before replacing concrete resolution.
- Confirmed the worker resolves definitions, sources, and state store from one async scope; an exception from one source does not suppress later sources.
- Confirmed ordinary failure preserves prior availability, explicit unavailability writes false, success resets failures and advances (never regresses or clears) the latest observation, and unconfigured state clears stale availability/error/failures.
- Confirmed every domain time comes from injected `IClock` or an upstream NodaTime value. No static now read was introduced.
- Confirmed the API state refresh occurs only after repository success and covers every successful disposition, including an unchanged cumulative snapshot.
- Confirmed the generic worker contains no provider-specific type, name, switch, factory, or special-case failure rule.
- Confirmed no pricing-plan work, provider registry, speculative dependency, or unrelated behavior change was added.

## Concerns

- The Release build still reports the pre-existing `xUnit1025` duplicate `InlineData` warning in `SpendEntriesEndpointsWafTests`; Task 4 does not alter that theory's data.
- EF CLI 10.0.8 reports that it is older than runtime 10.0.11. Migration generation and the pending-model check both succeeded; no tool upgrade was added to this task.
- `github-activity-api` is an explicit accepted design ruling for activity ingestion and deliberately remains distinct from the existing billing identity.
- No actionable Task 4 concern remains.

---

## Review remediation addendum — atomic freshness state and GitHub update time

Commit: final remediation SHA reported in the parent handoff because this addendum is part of that commit.

### Findings resolved

- Replaced every `SourceSyncStateStore` read/track/write transition with a PostgreSQL `INSERT ... ON CONFLICT DO UPDATE` statement. Concurrent first writes no longer race on the primary key; attempt/success/latest-observation instants use database-side maxima; failure increments are database-atomic.
- Preserved the transition rules exactly: unconfigured clears availability, failures, and error while retaining historical timestamps; attempt marks configured without resetting availability/error/failures; success marks available, clears error/failures, and never regresses timestamps; unavailable writes `IsAvailable = false`; ordinary failure retains the previous nullable availability.
- Passed nullable `Instant?` and `bool?` values directly through `ExecuteSqlInterpolatedAsync`. No untyped `DBNull.Value`, string-built SQL, registry, dependency, or additional abstraction was introduced.
- Moved `MarkAttemptAsync` inside the worker's per-source exception boundary. A state-write failure is sanitized/logged and cannot suppress later definitions or sources; cancellation still propagates from initial state writes, source calls, success writes, and failure writes.
- Carried GitHub PR `updated_at` through `GitHubPullRequestRecord` and included it when computing `LatestObservationAt`. An old open PR updated recently now advances source freshness.
- Reworked the unresolved-Key-Vault host test to assert the actual scoped `IEnumerable<IUsageSource>` and the matching unconfigured `SourceDefinition`, including the GitHub credential gate.
- Confirmed with `rg` that `GitHubIngestionService.IngestSinceAsync` had no production or test callers after converting its tests to `IUsageSource.IngestAsync`, then removed the bypass method. Rate limits and total configured-repository failure are now exercised only through the production contract.

### TDD evidence

| RED cycle | Expected failure observed | GREEN evidence |
| --- | --- | --- |
| Concurrent first success | 16 independent scopes produced PostgreSQL `23505` primary-key conflicts. | Atomic-upsert focused suite passed. |
| Concurrent failures | 24 independent failures persisted only 12 increments. | Final persisted count is exactly 24. |
| Monotonic concurrent success | Concurrent first writes collided before greatest timestamps could be retained. | `LastAttemptAt`, `LastSuccessAt`, and `LatestObservationAt` all retain the greatest of 24 writes. |
| Worker state-write isolation | A 101-character source ID caused `MarkAttemptAsync` to throw `22001` and abort the later source. | Invalid-source state failure is contained; the later source is polled and marked successful. |
| GitHub client seam | Focused compilation failed because `GitHubPullRequestRecord.UpdatedAt` did not exist. | Client parsing test carries the exact upstream instant. |
| GitHub freshness | An old open PR created in 2025 and updated in 2026 returned its 2025 creation time as latest observation. | The same test returns the 2026 update instant. |

### Verification

| Command | Result |
| --- | --- |
| Focused concurrency/worker/GitHub/host suite | Passed 59/59 against real PostgreSQL where applicable. |
| Focused `EventsEndpointsWafTests` | Passed 28/28 against real PostgreSQL; pre-existing `xUnit1025` warning only. |
| `dotnet ef migrations has-pending-model-changes --project src/AiObservatory.Data --startup-project src/AiObservatory.Api` | Passed: no model changes since the generated Task 4 migration. EF CLI/runtime patch-version notice remains. |
| `dotnet csharpier check .` | Passed, 181 files checked. |
| `dotnet build AiObservatory.slnx --configuration Release` | Succeeded, 0 errors; one pre-existing `xUnit1025` warning. |
| `dotnet test --solution AiObservatory.slnx --configuration Release --no-build --timeout 5m` | Passed 623/623. |
| `node --test clients/observatory-sweep.test.mjs` | Passed 32/32. |

### Files changed by remediation

- `src/AiObservatory.Data/Repositories/IGitHubActivityRepository.cs`
- `src/AiObservatory.Data/Repositories/SourceSyncStateStore.cs`
- `src/AiObservatory.Ingest/ProviderPollingWorkerService.cs`
- `src/AiObservatory.Ingest/Services/GitHub/GitHubActivityClient.cs`
- `src/AiObservatory.Ingest/Services/GitHub/GitHubIngestionService.cs`
- `tests/AiObservatory.Data.Tests/Repositories/GitHubActivityRepositoryTests.cs`
- `tests/AiObservatory.Ingest.Tests/IngestHostTests.cs`
- `tests/AiObservatory.Ingest.Tests/Services/GitHubActivityClientTests.cs`
- `tests/AiObservatory.Ingest.Tests/Services/GitHubIngestionServiceTests.cs`
- `tests/AiObservatory.Ingest.Tests/Services/ProviderPollingWorkerServiceTests.cs`
- `tests/AiObservatory.Ingest.Tests/Services/SourceSyncStateStoreConcurrencyTests.cs`

### Self-review and concerns

- Real PostgreSQL tests exercise independent `DbContext` scopes simultaneously; no mocked `DbSet`, provider substitution, or in-memory concurrency simulation is involved.
- State transitions remain one atomic database write each. The post-failure count read exists only to feed the retained three-failure escalation and cannot lose a persisted increment.
- The generic worker still contains no provider names/types and no GitHub-specific failure decision. All persisted/logged exception text passes through the existing sanitizer.
- The Task 4 migration, model, stable source identities, source definitions, credential semantics, API Created/Corrected/Unchanged behavior, and injected-NodaTime boundaries are unchanged.
- Known warnings remain the pre-existing duplicate-`InlineData` `xUnit1025` diagnostic and the EF CLI 10.0.8/runtime 10.0.11 notice. No actionable remediation concern remains.

---

## Whole-plan final slice — Google billing freshness and materialized concurrency barriers

Commit: final slice SHA reported in the parent handoff because this addendum is part of that commit.

### Findings resolved

- Google billing ingestion now reports the latest non-empty daily billing window it actually handled. The upstream contract is one requested `LocalDate` per response, so freshness uses that date at the same UTC-day-start boundary already used for `UsageEvent.OccurredAt`; it never uses the injected ingestion clock or invents sub-day precision.
- The inclusive range retains the greatest non-empty billing date even when an intermediate day is empty and a day's records arrive in any model order. A completely empty range still returns null.
- Each source-state concurrency test now materializes its task sequence with `ToArray()` before releasing the shared start barrier. A local entrant assertion proves all 16/24 delegates reached the barrier first; the test no longer relies on deferred LINQ enumeration that began only inside `Task.WhenAll`.
- No production source-state, migration, provider-registration, API, source-identity, or pricing behavior changed in this slice.

### TDD evidence

| RED cycle | Expected failure observed | GREEN evidence |
| --- | --- | --- |
| Google freshness | Both single-day and mixed non-empty/empty inclusive-range cases returned null instead of `2026-06-01T00:00:00Z` and `2026-06-03T00:00:00Z`. | Google service tests passed 3/3 with UTC-day-start freshness and the existing all-empty null case. |
| Concurrency barrier | All three tests reported zero entrants before `start.SetResult` (`0/16`, `0/24`, `0/24`), proving the lazy `Select` had not started any task. | After `ToArray`, all barrier participation assertions and real-PostgreSQL outcomes passed 3/3. |

### Verification

| Command | Result |
| --- | --- |
| Focused Google + source-state concurrency suite | Passed 6/6; concurrency uses independent scopes against real PostgreSQL. |
| `dotnet csharpier check .` | Passed, 181 files checked. |
| `dotnet build AiObservatory.slnx --configuration Release` | Succeeded, 0 errors; one pre-existing `xUnit1025` warning. |
| `dotnet test --solution AiObservatory.slnx --configuration Release --no-build --timeout 5m` | Passed 628/628. |
| `node --test clients/observatory-sweep.test.mjs` | Passed 37/37. |
| `dotnet ef migrations has-pending-model-changes --project src/AiObservatory.Data --startup-project src/AiObservatory.Api` | Passed: no pending model changes; the existing EF CLI/runtime patch-version notice remains. |

### Files changed by this slice

- `src/AiObservatory.Ingest/Services/Google/GoogleIngestionService.cs`
- `tests/AiObservatory.Ingest.Tests/Services/GoogleIngestionServiceTests.cs`
- `tests/AiObservatory.Ingest.Tests/Services/SourceSyncStateStoreConcurrencyTests.cs`
- `.superpowers/sdd/2026-08-24-source-aware-usage-foundation/task-4-report.md`

### Self-review and concerns

- Google freshness is derived only when at least one record exists for a requested day. Empty responses cannot advance freshness, and the greatest-date comparison makes the range result independent of record order.
- The barrier hardening adds only eager materialization and local participation counters; no helper, synchronization abstraction, production hook, or dependency was introduced.
- Known notices remain the pre-existing duplicate-`InlineData` `xUnit1025` diagnostic and EF CLI 10.0.8/runtime 10.0.11. No actionable finding remains in this slice.
