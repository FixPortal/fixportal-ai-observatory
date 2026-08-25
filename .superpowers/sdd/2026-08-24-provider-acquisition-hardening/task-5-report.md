# Task 5 — Google Cloud Billing BigQuery export

## Implementation

Replaced the nonexistent Cloud Billing HTTP reports route with the official `Google.Cloud.BigQuery.V2` 3.12.0 client. The new client validates the configured `project.dataset.table` before SQL interpolation, binds all time values as BigQuery timestamp parameters, queries affected stable billing groups for both the requested usage window and `export_time` corrections, converts each gross/credit line to integer micros before aggregation, fully buffers and validates result rows, and preserves normalized JSON evidence.

`GoogleBillingExportSource` reads the existing source watermark, writes only `google` / `google-cloud-billing-export` / `ProviderApi` / `Api` / `Billed` billing observations through `BillingObservationWriter`, and uses a length-prefixed SHA-256 key over date, invoice month, service ID, SKU ID, and currency. No usage events or daily aggregates are created. Host composition requires `GOOGLE_CLOUD_PROJECT_ID` and `GOOGLE_BILLING_EXPORT_TABLE` together, creates the BigQuery client lazily through a singleton disposable export client, and shares the existing FX/writer registration.

## Files changed

- `Directory.Packages.props`
- `README.md`
- `src/AiObservatory.Ingest/AiObservatory.Ingest.csproj`
- `src/AiObservatory.Ingest/Program.cs`
- `src/AiObservatory.Ingest/Services/Google/IGoogleBillingExportClient.cs`
- `src/AiObservatory.Ingest/Services/Google/GoogleBillingExportClient.cs`
- `src/AiObservatory.Ingest/Services/Google/GoogleBillingExportSource.cs`
- `src/AiObservatory.Ingest/Services/Google/GoogleBillingRecord.cs`
- Deleted obsolete `GoogleBillingClient`, `IGoogleBillingClient`, and `GoogleIngestionService`
- `tests/AiObservatory.Ingest.Tests/IngestHostTests.cs`
- `tests/AiObservatory.Ingest.Tests/Services/GoogleBillingExportClientTests.cs`
- `tests/AiObservatory.Ingest.Tests/Services/GoogleBillingExportSourcePostgresTests.cs`
- Deleted obsolete `GoogleIngestionServiceTests.cs`

## TDD evidence

- RED (initial boundary): `dotnet test tests\AiObservatory.Ingest.Tests\AiObservatory.Ingest.Tests.csproj --filter FullyQualifiedName~GoogleBillingExportClientTests` — 1 test failed as expected because `GoogleBillingExportClient` did not exist.
- RED (complete surface): `dotnet test tests\AiObservatory.Ingest.Tests\AiObservatory.Ingest.Tests.csproj --filter FullyQualifiedName~GoogleBilling` — expected compile failure before production types existed, naming missing export client/source/interface and expanded record fields.
- GREEN (first complete): same focused command — 20/20 passed.
- Review-finding RED: same focused command — 2 failures, exactly for use of `ROUND` instead of Google's integer-micros pattern and scoped rather than singleton lazy client registration.
- Review-finding GREEN: same focused command — 21/21 passed after stable-key query grouping, exact micros, explicit join, deterministic latest descriptions, and singleton/disposal changes.

## Verification

- `dotnet restore tests\AiObservatory.Ingest.Tests\AiObservatory.Ingest.Tests.csproj` — passed.
- `dotnet test tests\AiObservatory.Ingest.Tests\AiObservatory.Ingest.Tests.csproj --filter FullyQualifiedName~GoogleBilling --no-restore` — passed, 21/21.
- `dotnet test tests\AiObservatory.Ingest.Tests\AiObservatory.Ingest.Tests.csproj --no-restore` — passed, 369/369.
- `dotnet build src\AiObservatory.Ingest\AiObservatory.Ingest.csproj --configuration Release --no-restore` — passed, 0 warnings / 0 errors.
- `dotnet csharpier format` on the changed Ingest/Google and test files — passed.
- `git diff --check` — passed; only line-ending normalization notices.
- Stale-route scan: `rg` found no obsolete billing account setting, reports route, old Google client, or old source in production/README. The unrelated Google pricing-catalog endpoint remains intentionally.

Broader backend, Node/web, audit, NuGet, EF-model, CI-coverage, and secret-scan gates are intentionally left for the controller as requested.

## Self-review and concerns

- Verified the BigQuery join uses physical export fields in an explicit `ON` clause; no alias `USING` join remains.
- Stable group identity excludes mutable descriptions; descriptions are deterministically selected from the latest `export_time` row.
- The configured view/table must expose the documented Standard/Detailed common projection. Live ADC/IAM and query-charge validation require real Google credentials and remain deployment-time work.
- No commit was created and nothing was pushed.

## Fix round 1

- `GoogleBillingExportClientTests.cs`: RED command `dotnet test tests\AiObservatory.Ingest.Tests\AiObservatory.Ingest.Tests.csproj --filter FullyQualifiedName~GoogleBillingExportClientTests --no-restore` failed (1/20) because the SQL lacked the deterministic secondary latest-description ordering. The round also added regression coverage for null-safe stable-key joining, strict money types, invalid months, and immutable buffered results. GREEN with the same command passed 20/20.
- Corrected the full-table join to use `IS NOT DISTINCT FROM` over every stable dimension, so null key material reaches row validation rather than disappearing in SQL. Amount mapping now accepts only `BigQueryNumeric` (converted with `LossOfPrecisionHandling.Throw`) or `decimal`; null/string/double values fail closed. YYYYMM now validates calendar month bounds. Both buffer paths return `ImmutableArray`.
- SQL now orders latest descriptions by `export_time DESC, description DESC`, avoiding ambiguous ties.
- Scoped Release build: `dotnet build src\AiObservatory.Ingest\AiObservatory.Ingest.csproj --configuration Release --no-restore` passed, 0 warnings / 0 errors. CSharpier formatted the changed client/test files.
- Self-review: no direct `Google.Apis.Auth` reference was reintroduced. Fix edits remain unstaged and no commit was made.
- Source watermark regression: `GoogleBillingExportSourcePostgresTests.cs` now creates persisted source state, asserts exact `from`, exclusive-through, and `changesSince` client arguments, and verifies a July correction returned during an August range is retained. `dotnet test tests\AiObservatory.Ingest.Tests\AiObservatory.Ingest.Tests.csproj --filter FullyQualifiedName~GoogleBillingExportSourcePostgresTests --no-restore` passed 4/4.

## Fix round 1 — official SDK seam continuation

- Characterization rationale: the production `GetBillingRecordsAsync` path already buffered `BigQueryResults.GetRowsAsync()` and propagated query/enumeration failures and cancellation, so no production behavior change was required. The pre-change focused baseline passed 20/20. The first expanded seam run passed 26/27; its only failure was an invalid test assumption that Google’s HTTP layer preserves cancellation-token identity instead of using a linked token. The test was corrected to prove observable cancellation, with production untouched.
- `GoogleBillingExportClientTests.cs` now substitutes the official virtual `BigQueryClient.ExecuteQueryAsync` seam, constructs real public-for-testing `BigQueryResults` from REST `GetQueryResultsResponse`, `TableSchema`, and `TableRow` values, and exercises the production mapping/enumeration path. It proves exact parameter names, `BigQueryDbType.Timestamp`, exact UTC `DateTime` values, query cancellation-token propagation, query failure, two-page enumeration, later-page SDK failure, later-row mapping failure, cancellation during enumeration, and immutable return.
- The SDK’s later-page call is `internal virtual GetRawQueryResultsAsync(JobReference, GetQueryResultsOptions, DateTime?, CancellationToken)`, which external test assemblies cannot override. Later-page tests therefore use a real `BigQueryClientImpl` with an in-memory HTTP handler; no production query abstraction or factory was added.
- `GoogleBillingExportSourcePostgresTests.cs` now also proves the absent-state fallback passes the requested UTC `from` as `changesSince`, and the stored-watermark assertion now verifies the exact cancellation token as well as exact range/cursor arguments. Existing production behavior passed this characterization unchanged.
- GREEN: focused client 27/27; focused PostgreSQL source 5/5; combined Google 36/36. Release Ingest and Release test-project builds with `TreatWarningsAsErrors=true` both passed with 0 warnings / 0 errors. CSharpier formatted both touched test files.
- Files changed in this continuation: `tests/AiObservatory.Ingest.Tests/Services/GoogleBillingExportClientTests.cs`, `tests/AiObservatory.Ingest.Tests/Services/GoogleBillingExportSourcePostgresTests.cs`, and this report. Self-review found no production changes, new abstraction, staging, commit, push, or live Google dependency.

## Final review fix wave

- RED: `dotnet test tests\AiObservatory.Ingest.Tests\AiObservatory.Ingest.Tests.csproj --filter FullyQualifiedName~GoogleBillingExportClientTests --no-restore` failed 3/30, with all prior 27 tests passing. The failures proved the culture-aware parser accepted ` 02608`, `2026+8`, and `2026 8` as invoice identities.
- GREEN: added one pre-parse `char.IsAsciiDigit` guard while retaining the six-character and valid-month checks. The same focused command passed 30/30 immediately after the production fix; after the coverage additions it passed 42/42.
- Client coverage now rejects blank billing/service/SKU/currency identity text, lowercase/overlong currency, invalid raw JSON, invalid date values, and non-UTC `DateTime`/`DateTimeOffset` observations. SDK-representable values exercise the real `GetBillingRecordsAsync` / `BigQueryResults` path; non-UTC values use the existing narrow map helper because BigQuery TIMESTAMP conversion normalizes SDK rows to UTC.
- PostgreSQL characterization passed without further production changes: a five-case theory proves usage date, invoice month, service ID, SKU ID, and currency each produce two unique retained observation keys and spend entry keys. The watermark test now seeds an existing July identity and spend, returns the same identity with later `ObservedAt`, changed exact money, and changed raw JSON during an August request, then proves one corrected observation and one converged spend. A throwing-client test proves no `BillingObservation`, `SpendEntry`, `UsageEvent`, or `DailyAggregate` write.
- Verification: focused client 42/42; focused PostgreSQL source 11/11; combined Google/composition 57/57. Release Ingest and Release test-project builds with `TreatWarningsAsErrors=true` both passed with 0 warnings / 0 errors. CSharpier formatted and checked the three touched C# files; `git diff --check` passed.
- Files changed in this wave: `src/AiObservatory.Ingest/Services/Google/GoogleBillingExportClient.cs`, `tests/AiObservatory.Ingest.Tests/Services/GoogleBillingExportClientTests.cs`, `tests/AiObservatory.Ingest.Tests/Services/GoogleBillingExportSourcePostgresTests.cs`, and this report. Self-review found one production guard, no new abstraction, no deployment/infra edits, and no staging, commit, or push.

## Controller review and final verification

- Independent task review found two Critical and four Important issues. Fix round 1 addressed all six; a fresh scoped re-review found no new breakage.
- Final whole-task review found three Important validation/test-proof gaps. The single final fix wave addressed all three; a fresh scoped re-review found no new breakage.
- Fresh focused client/options/host composition: 91/91 passed.
- Fresh real PostgreSQL Google source/writer suite: 11/11 passed.
- `dotnet csharpier check .`: passed, 228 files.
- Release solution build: passed; only the documented pre-existing `xUnit1025` duplicate-InlineData warning at `SpendEntriesEndpointsWafTests.cs:813` remains outside the touched projects.
- Full Release backend: 987/987 passed, zero failed and zero skipped.
- Scoped Ingest and Ingest-test `TreatWarningsAsErrors` builds: both passed with zero warnings and zero errors.
- Node sweeper regression: 36/36 passed.
- Web Vitest: 214/214 passed across 30 files; lint passed with zero errors and seven pre-existing warnings; production build passed.
- `npm audit --audit-level=high`: zero vulnerabilities.
- NuGet direct/transitive vulnerability scan: no vulnerable packages across all seven projects.
- EF pending-model check: no changes; the existing EF tool 10.0.8/runtime 10.0.11 notice remains.
- CI gate coverage: all four jobs accounted for by `ci-gate`.
- Cached diff whitespace check, credential scan, SQL interpolation inspection, package-scope check, and README stale-claim scan passed. Direct `Google.Apis.Auth` references are gone; `Google.Cloud.BigQuery.V2` is pinned centrally at 3.12.0.
- Sole commit: `e054694abcf04aeebb6b054bf5bf2f15757d7267` — `fix(google): ingest Cloud Billing BigQuery export`. The post-commit worktree is clean and nothing was pushed.
