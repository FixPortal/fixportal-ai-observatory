# Performance sweep — 2026-08-29

## Accepted finding

`PERF-001` — "Aggregate delta pair issues four PostgreSQL round trips per repriced event where one upsert suffices".

- Audit: `E:\Documents\Obsidian Vault\Claude\Performance Audit\fixportal-ai-observatory\2026-08-29-0022-pricing-repricing-performance-audit.md` (+ `.manifest.json`, schema v2).
- Experiment record: `E:\Documents\Obsidian Vault\Claude\Performance Audit\fixportal-ai-observatory\2026-08-29-0052-PERF-001-experiment.md`.
- Approval: Chris approved this single finding and its exact proposed experiment ("go for it") after being shown the three published findings and the recommendation to run PERF-001 alone. PERF-002 and PERF-003 were explicitly excluded.
- Audited commit `9fca4f1d0ebaadaf7f83fb813753748540181086`; baseline commit identical (no drift); candidate on branch `performance/perf-001-net-aggregate-delta`.

## Change and rollback

`UsageRepository.UpdateEventPricingAsync` previously called `ApplyAggregateDeltaAsync(existing, -1)` and then `(existing, +1)` around the cost mutation. Each issued an `INSERT ... ON CONFLICT DO UPDATE` on `DailyAggregates` plus an `ExecuteDeleteAsync` `RequestCount = 0` cleanup — four round trips per repriced event.

Repricing mutates only `CostUsd` and `CacheSavingsUsd`, and neither is part of the conflict key `("Date", "Provider", "Model", "SourceId", "SourceKind", "UsageScope", "CostBasis")`. Both legs therefore always resolved to the same row and reduce to a single net delta.

The path now calls one new private `ApplyRepricingCostDeltaAsync` issuing a single upsert:

- `INSERT` branch carries the full event values with `RequestCount` 1 — the repair path when the aggregate row is missing, identical in effect to the old `+1` leg.
- `ON CONFLICT DO UPDATE` branch adds only net `CostUsd`, `UnknownCostCount`, `CacheSavingsUsd` and `UnknownCacheSavingsCount` deltas. `RequestCount` and all token columns are absent from the `SET` list, so this path can never decrement `RequestCount` and the zero-row cleanup is unreachable — hence dropped here.

`ApplyAggregateDeltaAsync` is **unchanged** and still serves the ingest correction path `ApplyLockedSnapshotAsync`, where `CopyCanonicalValues` can move key dimensions and the delta pair is genuinely not collapsible. Do not extend the net-delta form to that path.

Behavioural surface: the aggregate arithmetic. Rollback: revert the single change to `src/AiObservatory.Data/Repositories/UsageRepository.cs` (+51/−2). No migration, configuration change, data repair, or dependency is involved.

## Evidence

Baseline and candidate ran the identical command, in the same worktree, on the same harness, in the same session:

```
$env:TEST_DB_CONNECTION='Host=localhost;Port=55432;Database=postgres;Username=postgres;Password=postgres'
dotnet run --project tests/AiObservatory.Data.Tests/AiObservatory.Data.Tests.csproj --configuration Release -- --explicit only --filter-method AiObservatory.Data.Tests.Pricing.PricingRepricingServiceTests.QualificationRepricesEveryEligibleEventAndItsAggregate --show-live-output on --output Detailed
```

Environment: .NET SDK 10.0.400, net10.0 Release, Windows 10.0.26200 X64, ephemeral `postgres:17` with `shared_preload_libraries=pg_stat_statements`. Attribution instrument was `pg_stat_statements`, reset immediately before each run.

| Statistic | Baseline | Candidate | Delta |
| --- | --- | --- | --- |
| Median activation (1,000 events) | 7,104.3 ms | **5,079.3 ms** | **−28.5%** |
| Throughput | 140.8 events/s | 196.9 events/s | +39.8% |
| Activations | 6,407.0 / 7,104.3 / 7,846.6 ms | 5,056.4 / 5,112.3 / 5,079.3 ms | spread 20.3% → 1.1% of median |
| Statements, whole run | 41,387 | **29,387** | −12,000 (exactly 3 per event) |
| Round trips per repriced event | 10 | **7** | −30% |
| `INSERT` on `DailyAggregates` | 8,000 | 4,000 | halved |
| `DELETE` on `DailyAggregates` | 8,000 calls, 0 rows matched | **0** | eliminated |

Materiality threshold (at most 7 calls/event **and** at least 20% median improvement against a same-session baseline) is met on both counts. Predicted improvement from removing 3 round trips at the audited ~0.651 ms each was ~1.95 ms/event; measured was 2.03 ms/event — agreement within 4%.

Raw artefacts, with SHA-256, in `E:\Documents\Obsidian Vault\Claude\Performance Audit\fixportal-ai-observatory\2026-08-29-0022-raw\`: `perf001-baseline-stats.txt`, `perf001-candidate-stats.txt`, `perf001-candidate.diff`, `pg_stat_statements.txt`.

### Correctness and normal gates

| Gate | Command | Result |
| --- | --- | --- |
| Focused correctness | the qualification workload's own assertions (all 1,000 event prices, aggregate `CostUsd`, `RequestCount`) | passed |
| Data tests | `dotnet test tests/AiObservatory.Data.Tests/...` | 182 total, 0 failed |
| Format | `dotnet csharpier check .` | 244 files, exit 0 |
| Restore | `dotnet restore AiObservatory.slnx` | success |
| Build | `dotnet build AiObservatory.slnx --configuration Release --no-restore` | 0 errors, 1 pre-existing `S3776` warning in `PromptBuilder.cs` (untouched file) |
| Test | `dotnet test --solution AiObservatory.slnx --configuration Release --no-build --report-xunit-trx --results-directory ./TestResults --timeout 5m` | **1,050 total, 0 failed** |

Frontend gates were not applicable — no web source is touched.

Invariants protected by existing tests, all passing: `RepricingUpdatesOnlyEligibleEstimatesAndRepairsAggregateCoverage` (missing-aggregate repair, unknown-cost counting), `ActivationCallbackCommitsEffectiveDateRepricingWithTheSnapshot` (effective-date windows), `ActivationCallbackFailureRollsBackSnapshotEventsAndAggregates` (rollback on failure and cancellation), `EstimatedInsertPausedAcrossActivationCannotCommitTheOldPriceAfterActivation` (advisory-lock overlap). No new test was needed.

## Boundary and review

`test-managed-product-boundary.ps1 -BaseRef 9fca4f1 -ProductPath src` returned `passed: true`, zero violations, one `lexical-review` warning at `UsageRepository.cs:315`.

Disposition: accepted, not a violation. The warning flags the raw SQL string the guard cannot interpret. Every identifier in it is literal SQL; every interpolation hole is a typed CLR local; no `DBNull.Value` is passed. Verified by reading the executed statement back from the server — all values bind as `$1`–`$21`. Manual diff review confirms no `unsafe`, intrinsics, interop, native dependency, reflection, or runtime patching, and nothing moves outside the activation transaction or advisory lock.

Composition review (one frontier reviewer, verified read-only): Q1 restart-replay `clear`, Q2 idempotency `clear`, Q4 fence pairing `clear`, Q5 partial failure `clear`. Q5 independently confirms the dropped cleanup strands nothing; Q2 independently confirms the `INSERT`-branch repair path is value-identical to the old two-leg outcome.

**Open, and not addressed by this change:** Q3 ordering returned a `finding` that the reviewer states is **pre-existing and not introduced by this diff** — in the standalone (non-activation) reprice path, where no advisory lock is held, a concurrent estimated-ingest correction can land between the `AsNoTracking` read and the locked write, so a price is computed from tokens the event no longer has. Event and aggregate stay mutually consistent but both wrong until the next daily pass recomputes and self-heals. This is carried forward for a separate decision; it blocks a `quality-gate-review` PASS until fixed or explicitly accepted.

Displaced costs: none identified. The change removes work rather than trading it — no new allocation, cache, retained memory, connection or configuration. A shorter `pg_advisory_xact_lock` hold also lowers the contention ceiling for concurrent ingest during an activation.

Commercial impact: `currencyCost: unknown`. No repricing volume, deployment topology, unit rate, utilisation assumption, or activation SLO was supplied, and the saving crosses no evidenced capacity, replica, SKU or billing boundary.

## Benchmark

`not retained` as a new artefact. The workload already lives in the repository as the opt-in `[Fact(Explicit = true)]` qualification added by PR #196 and is reused as-is. No CI performance gate was added and none is proposed.
