# Task 1 report — lossless provider-billing write path

## Delivery

- Base: `70b095af02577396208b55c8fdc4a1031e252120`
- Delivery commit: the commit containing this report; its SHA is recorded in the Task 1 handoff after commit creation.
- Scope: Task 1 only. No push performed.

`BillingObservation` now retains normalized provider billing facts independently of the spend ledger. `BillingObservationWriter` is the single correction boundary for those facts and their derived non-zero `SpendEntry`. The existing FX implementation moved from API to Data, and GitHub billing now delegates only its final persistence to the writer while retaining its existing product mapping, year selection, per-line failure isolation, and logging.

## RED / GREEN

RED was recorded before production changes: the focused Data test project failed to compile because `BillingObservation`, `BillingObservationWriter`, and `BillingWriteDisposition` did not exist (`CS0246` / `CS0103`).

GREEN evidence:

- Baseline: Usage migration `2/2`, FX/GitHub unit `42/42`, GitHub PostgreSQL integration `6/6`.
- Writer and migration PostgreSQL matrix: `23/23` after the final tiny-rounding case.
- Refactored FX/GitHub/worker-arm unit lane: `32/32`.
- Refactored GitHub PostgreSQL integration lane: `6/6`.
- Ingest composition regression lane: `15/15`.
- Full Release backend: `819/819`, zero failed and zero skipped.
- Observatory sweep Node regression: `36/36`.
- Web Vitest: `214/214` across 30 files.

The PostgreSQL writer matrix covers create, semantic exact replay/no-op, correction, correction to zero, retained initial zero, refund sign and date-correct FX, sub-precision GBP rejection, raw evidence/provenance, missing vendor/category/FX rollback, invalid trust-boundary input, duplicate concurrent identity convergence, collision-safe overlong keys, cancellation, manual recategorization preservation, legacy API-row adoption, JSONB behavior, and database constraints.

## Entity, migration, and transaction

The generated ordered migration is `20260825033709_AddBillingObservations`. It creates `BillingObservations` with JSONB evidence, exact amount arithmetic and provenance/normalization checks, a unique `(SourceId, ObservationKey)` index, and an `OccurredOn` index. It widens `SpendEntries.SourceId` to 200 characters and seeds the fixed `api-usage` category required by the following OpenAI and Anthropic cost-source tasks. The snapshot matches the runtime model, and EF reports no pending model changes.

The writer validates all identity, provenance, currency, arithmetic, and JSON evidence before starting database work. It resolves and freezes FX before opening the transaction. Inside one PostgreSQL transaction it takes an identity-scoped advisory lock, then inserts/no-ops/corrects the observation and creates/updates/removes only the matching `SpendSource.Api` ledger row. Corrections preserve manually edited vendor and category IDs. Zero net retains the observation and creates no spend; correction to zero removes only its keyed API spend. Normal entry keys remain `billing:<sourceId>:<observationKey>`; overlong keys use SHA-256 over length-prefixed source/key material rather than truncation.

GitHub net-only wire facts are normalized as gross = net and credits = 0, with scope `Mixed`, billed/provider-API provenance, stable source/key identity, and raw JSON evidence. Created zero lines are retained but do not inflate the historical “written entries” count; a correction to zero counts as a correction while deleting the derived spend.

## Gate results

- `dotnet csharpier check .`: pass, 215 files.
- Release solution build: pass.
- Full Release backend test suite: pass, `819/819`.
- `node --test clients/observatory-sweep.test.mjs`: pass, `36/36`.
- `dotnet ef migrations has-pending-model-changes`: pass, no pending model changes.
- `dotnet list ... package --vulnerable --include-transitive`: pass, no vulnerable packages in all seven projects.
- `npm run lint`: pass with zero errors and seven pre-existing warnings.
- `npm test`: pass, `214/214`.
- `npm run build`: pass, TypeScript and Vite production build.
- `npm audit --audit-level=high`: pass, zero vulnerabilities.
- CI gate-coverage assertion: pass, all four jobs accounted for.
- `git diff --check`: pass; Git reports only the existing working-copy LF-to-CRLF notices for two XML project files.
- Diff credential scan: pass; no added private key, token, password, or connection-string material.

## Concerns and deliberate deviations

- `Microsoft.Extensions.Caching.Memory` is pinned at `10.0.11`, not the plan's stale illustrative `10.0.10`: restore proved EF Core `10.0.11` requires Memory `>= 10.0.11` (`NU1109` otherwise). This is the only new direct dependency.
- The shared Data registration does not register the writer. It is registered in the API composition root beside the configured FX client. Registering it in `AddDataLayer` broke 14 Ingest-host tests because the Task 1 Ingest root intentionally has no FX client yet; later provider-cost tasks can register both together rather than acquiring an implicit default HTTP client.
- `dotnet format ... analyzers --verify-no-changes` reports the pre-existing `xUnit1025` duplicate-inline-data warning at `SpendEntriesEndpointsWafTests.cs:813`. The same warning appears in the otherwise-green Release build. It is unrelated to Task 1 and was left untouched.
- Ponytail full: no provider framework, repository/unit-of-work layer, event bus, one-implementation interface, or speculative dependency was added. The concrete writer and database constraints are the smallest shared boundary that protects this money path.

## Review follow-up — preserve frozen FX on exact replay

Review identified that an exact non-zero billing replay still fetched the current historical rate and compared it with the ledger's frozen rate. A changed provider rate alone therefore produced `Corrected` and overwrote `FxRate`, `AmountGbp`, and `RecordedAt`, despite unchanged billing facts.

The real-PostgreSQL regression was added first. RED reproduced the defect with an initial `0.75` rate and an identical replay at `0.79`: the writer returned `Corrected` instead of `Unchanged`. The minimal fix excludes frozen FX and GBP fields from the unchanged-provider-facts comparison. A genuine provider-fact correction still resolves and freezes its replacement FX before the transaction.

Follow-up GREEN evidence:

- Frozen-FX replay regression: `1/1`; disposition `Unchanged`, `FxRate` remains `0.75`, `AmountGbp` remains `6.00`, and `RecordedAt` remains unchanged.
- PostgreSQL writer and migration lane: `24/24`, retaining correction, zero-net, refund, concurrency, rollback, and legacy-convergence coverage.
- Focused FX/GitHub/worker-arm unit lane: `32/32`.
- GitHub PostgreSQL integration lane: `6/6`.
- Release solution build: pass with zero warnings and zero errors.
- Full Release backend: `820/820`, zero failed and zero skipped.
- CSharpier and staged diff checks: pass.

Follow-up commit: the commit containing this section; its SHA is recorded in the Task 1 handoff after commit creation.
