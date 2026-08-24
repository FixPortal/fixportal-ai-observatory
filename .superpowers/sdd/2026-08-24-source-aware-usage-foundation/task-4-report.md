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
