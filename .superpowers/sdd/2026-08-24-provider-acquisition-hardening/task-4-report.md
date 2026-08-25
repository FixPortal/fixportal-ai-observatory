# Task 4 report — current Copilot organization reports

## Delivery

- Approved base: `60f74a54ca25d2c049829e9819bf4791d559f35d` (`60f74a5`). This controller-approved Task 3 head supersedes the task brief's formerly stale base line.
- No commit was created, no file was staged, and nothing was pushed. The controller's single-commit instruction supersedes the brief's commit handoff sentence.
- Scope remained Task 4 only. Google, pricing renewal, `ProviderPollingWorkerService`, packages, dashboards, and deployment were not changed. Existing `copilot-local/Subscription/Notional` telemetry remains separate and unchanged.

## What was implemented

- Replaced the retired inline Copilot metrics client/source with `ICopilotReportClient`, `CopilotReportClient`, `CopilotDailyReportRecord`, and `CopilotReportSource`.
- The descriptor client uses `GET /orgs/{org}/copilot/metrics/reports/organization-28-day/latest`, `Accept: application/vnd.github+json`, `X-GitHub-Api-Version: 2026-03-10`, a bearer token, and the configured organization. A separate client downloads signed HTTPS links without GitHub-only headers.
- The descriptor is bounded to 2 MiB. All unique, absolute, credential-free HTTPS downloads are completed under one actual-byte 50 MiB report budget before any immutable records are returned. NDJSON is read line-by-line as strict UTF-8, with at most one UTF-8 BOM accepted on the first report line. Validation covers required consumed wrapper, identity, and normalized fields; unconsumed current/future facts are retained raw rather than subjected to a second GitHub JSON Schema validator.
- Both official wrapper forms are supported: a valid UTC `created_at` supplies `ObservedAt`; when `created_at` is absent, the source uses one injected acquisition instant for the complete report. No fallback timestamp is inserted into raw evidence.
- Current daily, weekly, and monthly active-user fields must be present, non-null, integral, and nonnegative at the trust boundary. Per the controller ruling, normalized CLR/storage fields are nullable `int?` for compatibility, with null-tolerant database constraints.
- Each retained row carries faithful per-day evidence: immutable wrapper identity/window/optional `created_at` metadata plus only that row's `day_total`. A correction to one day therefore does not rewrite unrelated daily rows.
- Added `CopilotDailyReport` and an EF-generated migration with `jsonb` evidence, provenance defaults/constraints, nonnegative fact constraints, a day index, and a nonfiltered unique `(SourceId, ReportKey)` index. The collision-safe stable key hashes the length-prefixed provider organization identity plus day and excludes mutable facts and timestamps.
- The source validates the complete client result before persistence, filters the requested inclusive day range, and performs inserts/corrections in one `SaveChangesAsync` transaction. Exact replay is a semantic JSON no-op, history outside the rolling window is untouched, and the returned timestamp is the greatest actual persisted observation time.
- Composition requires both valid `GITHUB_TOKEN` and `COPILOT_ORG` values, rejects whitespace and unresolved Key Vault literals, keeps GitHub Activity independently gated, and documents current permissions and engagement-only semantics.
- Real PostgreSQL tests prove correction/replay/rollback behavior, database constraints and `jsonb`, and that no `UsageEvent`, `DailyAggregate`, `BillingObservation`, or `SpendEntry` is fabricated.

## TDD RED / GREEN evidence

### Existing focused baseline

Command:

```text
dotnet test tests/AiObservatory.Ingest.Tests/AiObservatory.Ingest.Tests.csproj --configuration Release --filter "FullyQualifiedName~Copilot"
```

Result: passed `2/2`, failed `0`, skipped `0`, duration `665 ms`.

### Initial expanded RED

The client/source/composition tests were written before production edits. They explicitly pinned both official wrapper shapes: one with valid UTC `created_at`, and one without `created_at` that uses one injected acquisition instant while raw evidence remains timestamp-free.

Command:

```text
dotnet test tests/AiObservatory.Ingest.Tests/AiObservatory.Ingest.Tests.csproj --configuration Release --filter "FullyQualifiedName~Copilot"
```

Result: exit `1`; compilation failed with expected `CS0246` errors because `CopilotReportClient` and the replacement report types did not yet exist. This was the expected RED for the wished-for replacement contract.

### First meaningful GREEN

Command:

```text
dotnet test tests/AiObservatory.Ingest.Tests/AiObservatory.Ingest.Tests.csproj --configuration Release --filter "FullyQualifiedName~Copilot"
```

Result: passed `41/41`, failed `0`, skipped `0`, duration `8.907 s`.

### Self-review replay RED / GREEN

An added replay assertion proved that a timestamp-less exact replay must return the stored observation rather than a later acquisition instant.

RED command:

```text
dotnet test tests/AiObservatory.Ingest.Tests/AiObservatory.Ingest.Tests.csproj --configuration Release --filter "FullyQualifiedName~AtomicallyCorrectsStableIdentity"
```

RED result: failed `1/1`; expected `2026-08-22T12:00:00Z`, but received `2026-08-23T12:00:00Z`. After returning the persisted row's timestamp for a no-op replay, the same regression passed `1/1`.

### Review RED / GREEN

Before addressing the review findings, two regressions were added: an oversized descriptor must fail before any signed download, and correcting one of two daily totals must not update the other day's evidence or observation.

Command:

```text
dotnet test tests/AiObservatory.Ingest.Tests/AiObservatory.Ingest.Tests.csproj --configuration Release --filter "FullyQualifiedName~OversizedDescriptor|FullyQualifiedName~TwoDayCorrection"
```

RED result: failed `2/2`. The oversized descriptor incorrectly reached the download handler (`InvalidOperationException` instead of `InvalidDataException`), and the unchanged second day's observation advanced from `2026-08-22T12:00:00Z` to `2026-08-23T12:00:00Z` because each row held the whole wrapper.

GREEN result after the 2 MiB descriptor bound and per-day evidence change: passed `2/2`, failed `0`, skipped `0`, duration `6.109 s`.

Final focused command:

```text
dotnet test tests/AiObservatory.Ingest.Tests/AiObservatory.Ingest.Tests.csproj --configuration Release --filter "FullyQualifiedName~Copilot"
```

Result: passed `43/43`, failed `0`, skipped `0`, duration `9.189 s` in the final fresh verification run.

## Migration and model verification

- `dotnet ef migrations add AddCopilotDailyReports --project src/AiObservatory.Data --startup-project src/AiObservatory.Api`: succeeded and generated timestamped migration `20260825062334_AddCopilotDailyReports` plus designer/snapshot updates after the current model. EF reported the existing tool `10.0.8` versus runtime `10.0.11` notice.
- `dotnet test tests/AiObservatory.Data.Tests/AiObservatory.Data.Tests.csproj --configuration Release --filter "FullyQualifiedName~UsageMigrationTests"`: passed `2/2`, failed `0`, skipped `0`, duration `7.299 s`.
- `dotnet ef migrations has-pending-model-changes --project src/AiObservatory.Data --startup-project src/AiObservatory.Api`: build succeeded; no changes were found since the last migration. The same EF tool/runtime notice was emitted.
- `dotnet ef migrations script 20260825033709_AddBillingObservations 20260825062334_AddCopilotDailyReports --project src/AiObservatory.Data --startup-project src/AiObservatory.Api --no-build`: succeeded. The generated SQL was inspected and contains a transaction, the new table, nullable active-user columns, all checks, the day index, the nonfiltered unique index, and commit.

## Expanded and full verification

- `dotnet build AiObservatory.slnx --configuration Release`: passed with `0` errors and one pre-existing `xUnit1025` duplicate-InlineData warning at `SpendEntriesEndpointsWafTests.cs:813`.
- `dotnet test --solution AiObservatory.slnx --configuration Release --no-build --timeout 5m`: passed `929/929`, failed `0`, skipped `0`, duration `29.841 s`.
- `dotnet build src/AiObservatory.Ingest/AiObservatory.Ingest.csproj --configuration Release --no-restore -p:TreatWarningsAsErrors=true`: passed with `0` warnings and `0` errors.
- `dotnet build tests/AiObservatory.Ingest.Tests/AiObservatory.Ingest.Tests.csproj --configuration Release --no-restore -p:TreatWarningsAsErrors=true`: passed with `0` warnings and `0` errors.
- `dotnet csharpier format .`: completed; a second run made no further changes.
- `dotnet csharpier check .`: passed, `227` files checked in `856 ms`.
- `node --test clients/observatory-sweep.test.mjs`: passed `36/36`, failed `0`, skipped `0`, approximately `662 ms`.
- `npm run lint` from `src/AiObservatory.Web`: passed with `0` errors and seven pre-existing warnings.
- `npm test` from `src/AiObservatory.Web`: passed `214/214` tests across `30` files in `11.65 s`.
- `npm run build` from `src/AiObservatory.Web`: passed; `1021` modules transformed in approximately `390 ms`.
- `npm audit --audit-level=high` from `src/AiObservatory.Web`: passed with `0` vulnerabilities.
- `dotnet list AiObservatory.slnx package --vulnerable --include-transitive`: passed; all seven projects reported no vulnerable direct or transitive packages.
- `python .github/scripts/assert_gate_coverage.py .github/workflows/ci.yml`: passed; all four jobs are accounted for by `ci-gate`.
- `git diff --check`: passed; only working-copy LF-to-CRLF notices for `README.md` and the Ingest test project were emitted.
- Trailing-whitespace scan over every untracked Task 4 file: no matches.
- Credential scan over every Task 4 production/test/doc path for GitHub PATs, private keys, client secrets, passwords, and connection strings: no matches.
- Copilot production logging scan for tokens, signed links/URLs/URIs, requests, responses, or console writes: no matches. The sole source log contains only the retained row count.
- `git rev-parse HEAD`: `60f74a54ca25d2c049829e9819bf4791d559f35d`.
- `git diff --cached --stat`: empty; nothing is staged.

## Files changed

Modified:

- `README.md`
- `src/AiObservatory.Data/AiObservatoryDbContext.cs`
- `src/AiObservatory.Data/Migrations/AiObservatoryDbContextModelSnapshot.cs`
- `src/AiObservatory.Ingest/Program.cs`
- `tests/AiObservatory.Ingest.Tests/AiObservatory.Ingest.Tests.csproj`
- `tests/AiObservatory.Ingest.Tests/IngestHostTests.cs`

Added:

- `src/AiObservatory.Data/Entities/CopilotDailyReport.cs`
- `src/AiObservatory.Data/Migrations/20260825062334_AddCopilotDailyReports.cs`
- `src/AiObservatory.Data/Migrations/20260825062334_AddCopilotDailyReports.Designer.cs`
- `src/AiObservatory.Ingest/Services/Copilot/CopilotDailyReportRecord.cs`
- `src/AiObservatory.Ingest/Services/Copilot/CopilotReportClient.cs`
- `src/AiObservatory.Ingest/Services/Copilot/CopilotReportSource.cs`
- `src/AiObservatory.Ingest/Services/Copilot/ICopilotReportClient.cs`
- `tests/AiObservatory.Ingest.Tests/Services/CopilotReportClientTests.cs`
- `tests/AiObservatory.Ingest.Tests/Services/CopilotReportSourceTests.cs`
- `.superpowers/sdd/2026-08-24-provider-acquisition-hardening/task-4-report.md`

Removed as retired:

- `src/AiObservatory.Ingest/Services/Copilot/CopilotIngestionService.cs`
- `src/AiObservatory.Ingest/Services/Copilot/CopilotUsageClient.cs`
- `src/AiObservatory.Ingest/Services/Copilot/CopilotUsageRecord.cs`
- `src/AiObservatory.Ingest/Services/Copilot/ICopilotUsageClient.cs`
- `tests/AiObservatory.Ingest.Tests/Services/CopilotIngestionServiceTests.cs`

## Self-review and concerns

- Self-review found and fixed the exact-replay timestamp bug before final verification.
- Controller review found and the RED/GREEN cycle fixed whole-wrapper duplication and the unbounded descriptor body. Per-day evidence now prevents unrelated correction churn while preserving auditable wrapper identity/window/optional timestamp facts.
- Cancellation, complete-before-return behavior, signed-link header isolation, stable identity, correction atomicity, historical retention, and non-fabrication boundaries are exercised directly.
- Ponytail full: reused existing HTTP, JSON, source, EF, DI, configuration-gate, clock, and PostgreSQL fixture patterns. No generic provider framework, speculative abstraction, new package, retry layer, or polling-worker branch was introduced.
- No unresolved product concern remains. Non-blocking repository/tooling notices are the pre-existing xUnit warning, seven pre-existing web lint warnings, two existing line-ending notices, and the EF tool/runtime patch-version notice.

## Fix round 1 — transport encoding, signed-link logging, and actual-byte coverage

### Tests added first

- `tests/AiObservatory.Ingest.Tests/Services/CopilotReportClientTests.cs` now proves one UTF-8 BOM is accepted only at the first report line, UTF-16/UTF-32 BOM-selected transports are rejected, and a generated non-buffered response with no `Content-Length` fails closed after actual bytes exceed the aggregate 50 MiB budget.
- `tests/AiObservatory.Ingest.Tests/IngestHostTests.cs` now resolves the real composed `CopilotSignedDownloads` handler chain and proves it contains no default logging handler.

RED command:

```text
dotnet test tests/AiObservatory.Ingest.Tests/AiObservatory.Ingest.Tests.csproj --configuration Release --filter "FullyQualifiedName~Bom|FullyQualifiedName~ActualUndeclared|FullyQualifiedName~NoLoggingHandlers"
```

RED result: `5` total, `3` failed, `2` passed, exit `2`, duration `4.782 s`. UTF-16 and UTF-32 returned records instead of throwing `InvalidDataException`; the composed signed-download chain contained `LoggingScopeHttpMessageHandler` and `LoggingHttpMessageHandler`. UTF-8-BOM acceptance and the generated no-length actual-byte cap characterization already passed, confirming that the existing streaming budget was correct and needed coverage rather than a production change.

Minimal production fixes:

- `CopilotReportClient` disables automatic BOM-selected encoding, explicitly removes one optional UTF-8 BOM only from the first report line, and maps strict decoder failures to `InvalidDataException`.
- `Program.cs` registers `CopilotSignedDownloads` with `.RemoveAllLoggers()`, eliminating query-bearing signed URLs from the default `IHttpClientFactory` logging pipeline independently of runtime redaction configuration.
- A small concrete `ReadDownloadAsync` extraction kept the strict transport logic readable and removed the analyzer complexity warning introduced by the initial inline GREEN. No generic cap or transport abstraction was added.

First GREEN command: the same focused command above.

First GREEN result: `5/5` passed, failed `0`, skipped `0`, duration `4.770 s`; it exposed an `S3776` complexity warning in the inline implementation. After the behavior-preserving extraction, the same command passed `5/5` with no warning in `4.692 s`.

Verification:

- `dotnet test tests/AiObservatory.Ingest.Tests/AiObservatory.Ingest.Tests.csproj --configuration Release --filter "FullyQualifiedName~Copilot"`: passed `48/48`, failed `0`, skipped `0`, duration `9.094 s`.
- `dotnet csharpier format src/AiObservatory.Ingest/Program.cs src/AiObservatory.Ingest/Services/Copilot/CopilotReportClient.cs tests/AiObservatory.Ingest.Tests/IngestHostTests.cs tests/AiObservatory.Ingest.Tests/Services/CopilotReportClientTests.cs`: formatted four changed files.
- `dotnet csharpier check .`: initially passed after the four-file format (`227` files in `862 ms`). A later explicit no-`Content-Length` fixture assertion made one test layout stale; the check reported that single file, `dotnet csharpier format tests/AiObservatory.Ingest.Tests/Services/CopilotReportClientTests.cs` formatted it, and the final verification check passed `227` files in `860 ms`.
- `dotnet build src/AiObservatory.Ingest/AiObservatory.Ingest.csproj --configuration Release --no-restore -p:TreatWarningsAsErrors=true`: final verification passed with `0` warnings and `0` errors in `0.71 s`.
- `dotnet build tests/AiObservatory.Ingest.Tests/AiObservatory.Ingest.Tests.csproj --configuration Release --no-restore -p:TreatWarningsAsErrors=true`: final verification passed with `0` warnings and `0` errors in `0.83 s`.
- The first attempt to invoke those two builds concurrently caused the Ingest compiler to report `CS2012` on its shared `obj` output while the test-project build passed. This was command contention, not a code failure; both commands were rerun discretely and passed as recorded above.
- Final fresh `dotnet test tests/AiObservatory.Ingest.Tests/AiObservatory.Ingest.Tests.csproj --configuration Release --no-build --filter "FullyQualifiedName~Copilot"`: passed `48/48`, failed `0`, skipped `0`, duration `9.242 s`.

Files changed in fix round 1:

- `src/AiObservatory.Ingest/Program.cs`
- `src/AiObservatory.Ingest/Services/Copilot/CopilotReportClient.cs`
- `tests/AiObservatory.Ingest.Tests/IngestHostTests.cs`
- `tests/AiObservatory.Ingest.Tests/Services/CopilotReportClientTests.cs`
- `.superpowers/sdd/2026-08-24-provider-acquisition-hardening/task-4-report.md`

Self-review: the strict UTF-8 decoder now rejects UTF-16/UTF-32 before JSON parsing; exactly one leading UTF-8 BOM can be removed across the complete multi-file report; later BOMs remain invalid JSON. The signed-download client has no framework logging handlers and still carries no GitHub headers. The over-limit fixture is non-seekable, generated incrementally, has no declared length, and exercises the actual `BudgetedReadStream` byte counter without adding a production configuration or generic abstraction. The controller subsequently staged this fix round for scoped re-review; no commit or push had occurred at that point.

## Controller final verification and review

- Independent task review found three Important trust-boundary gaps: broad BOM-selected decoding, default signed-download HTTP loggers, and absent actual-byte budget coverage. Fix round 1 addressed all three under the RED/GREEN evidence above.
- Scoped re-review verdict: all findings addressed, no new Critical/Important breakage, and no out-of-scope observations.
- Final `dotnet csharpier check .`: passed, `227` files.
- Final `dotnet build AiObservatory.slnx --configuration Release`: passed with `0` warnings and `0` errors.
- Final `dotnet test --solution AiObservatory.slnx --configuration Release --no-build --timeout 5m`: passed `934/934`, failed `0`, skipped `0`.
- Final Node sweep: passed `36/36`; final web tests: passed `214/214`; web lint remained `0` errors with seven pre-existing warnings; web production build passed.
- Final `npm audit --audit-level=high`: `0` vulnerabilities; NuGet direct/transitive vulnerability scan remained clean across all seven projects; CI gate coverage still accounted for all four jobs.
- Final EF pending-model check found no changes since `20260825062334_AddCopilotDailyReports`; the existing EF tool/runtime patch notice remained.
