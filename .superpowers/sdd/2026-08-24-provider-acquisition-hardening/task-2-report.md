# Task 2 report — separate OpenAI usage activity from billed costs

## Delivery

- Base: `94e065c3a90b185ffc6a808305a95b4c40f7320a`.
- Delivery commit: the commit containing this report; its SHA is recorded in the Task 2 handoff after commit creation.
- Scope: Task 2 only. `ProviderPollingWorkerService` was not edited. No push performed.

OpenAI organization usage and organization costs now share one provider-local admin client but remain two independent ingestion sources. Usage records produce only `Api/ListPriceEstimate` usage events through the central price resolver. Cost records produce only `ProviderApi/Api/Billed` billing observations through `BillingObservationWriter`; they do not enter usage aggregates.

## Official wire contract and acquisition boundary

The implementation was checked against the current OpenAI Administration API reference on `developers.openai.com` on 2026-08-25. It requests the complete inclusive date range from `GET /v1/organization/usage/completions`, grouped by `model`, `batch`, and `service_tier`, and independently from `GET /v1/organization/costs`, grouped by `project_id` and `line_item`.

`OpenAiAdminClient` accumulates nothing visible to a caller until every page and record has validated. Both lanes require the documented object discriminators, daily in-range buckets, `has_more`, and `next_page`; continuing cursors must be nonblank and unique, final cursors must be null, and pagination stops with an error before a 10,001st request. Failed HTTP responses, malformed JSON, malformed results, oversized responses, invalid dates, negative or inconsistent token lanes, invalid monetary facts, and unsupported currencies all throw instead of returning a prefix. Each response is streamed through a dependency-free 2 MiB bound. Cancellation is propagated. Cursor query strings, credentials, and response bodies are never logged.

Usage retains upstream bucket bounds, exact model/batch/service-tier dimensions, uncached/cache-read/cache-write/output lanes, request count, and raw bucket/result evidence. `processing` is derived only from exact documented values: batch, default, flex, or priority; incomplete or unknown dimensions remain null. Context and region are absent because the endpoint does not expose them.

Costs use `amount.value` directly as a decimal monetary amount, accept and normalize only USD, and retain line item, project, quantity, quantity unit, exact bucket bounds, and raw evidence. Gross and net equal the provider amount and credits are zero.

## Source semantics and correction identity

`OpenAiUsageSource` groups exact upstream price dimensions, writes stable `openai-usage-api` events atomically through `RecordEstimatedEventAsync`, and reports the latest upstream bucket end. Event identity hashes length-prefixed bucket/model/batch/service-tier facts. Derived processing is intentionally not a second identity fact: correcting a mapping cannot create a duplicate event. Missing provider price dimensions continue to resolve to null centrally; the source contains no fallback prices.

`OpenAiCostsSource` maps every financial result to `BillingObservationWriter` under vendor `openai` and category `api-usage`, retains zero observations, and reports the latest upstream bucket end. Observation identity hashes the requested upstream grouping facts: bucket, project, and line item. Quantity and unit remain evidence but not identity, so a provider backfill or correction updates the same observation.

A real-PostgreSQL regression was added after review of that identity boundary. RED showed that including `quantity_unit` made a provider correction create two observations instead of one. Removing the descriptive unit from identity made the correction converge on one observation and the regression passed. The matching usage key was also kept to upstream dimensions rather than the derived processing label.

The Ingest composition root always exposes the two OpenAI `SourceDefinition` rows. When and only when `OPENAI_ADMIN_KEY` resolves to a real credential, it registers one typed `IOpenAiAdminClient`, two scoped sources, memory cache, the 10-second `FxRateProvider` client, and `BillingObservationWriter`. Unconfigured, blank, and unresolved Key Vault values register neither source implementation. Host tests prove two definitions/two sources, one shared client instance within a scope, resolvable FX/writer composition, and no credential exposure.

## RED / GREEN

- Baseline before production edits: OpenAI, Ingest-host, and provider-worker lane `69/69`.
- First RED: the new client contract tests failed to compile because `OpenAiAdminClient` did not exist (`CS0246`). Source tests then stayed RED while the old one-lane client/service contract remained.
- Client GREEN: `19/19`, covering official usage/cost shapes, independent multi-page success and exact second-page cursors, repeated/missing/final cursors, the 10,000-page cap, failed/malformed/oversized middle pages, cancellation, bucket/range validation, token lanes, decimal costs, and currency validation. The cap case completes in about one second with the in-memory handler.
- Usage-source GREEN: `3/3`, covering exact lanes/dimensions/evidence, batch/tier separation, stable correction identity, null central price resolution, and latest upstream instant.
- Real-PostgreSQL cost-source GREEN: `3/3`, covering billed observation plus derived spend, no usage-aggregate mutation, zero retention, replay/no-op, correction identity, project/line-item evidence, and latest upstream instant.
- Final combined OpenAI, worker, and host lane: `87/87`.
- Data money/migration regression lane: `57/57`.

## Final gates

- `dotnet csharpier check .`: pass, 218 files.
- Release solution build: pass, zero warnings and zero errors.
- Full Release backend: pass, `838/838`, zero failed and zero skipped.
- Scoped Ingest and Ingest-test analyzer gates: pass with no findings. The whole-solution analyzer gate still reports only the pre-existing `xUnit1025` at `SpendEntriesEndpointsWafTests.cs:813`; it is unrelated and untouched.
- Observatory sweep Node regression: pass, `36/36`.
- Web Vitest: pass, `214/214` across 30 files.
- Web lint: pass with zero errors and seven pre-existing warnings.
- Web TypeScript/Vite production build: pass.
- `npm audit --audit-level=high`: pass, zero vulnerabilities.
- EF pending-model check: pass, no model changes. The existing EF tool `10.0.8` versus runtime `10.0.11` notice remains.
- NuGet vulnerable-package check: pass, no vulnerable direct or transitive packages in all seven projects.
- CI gate-coverage assertion: pass, all four jobs accounted for.
- Cached diff whitespace and credential scans: pass; no added private key, token, password, or connection-string material.

## Ponytail full

No universal provider abstraction, generic pagination framework, repository/unit-of-work layer, event bus, fallback pricing table, package, or polling-worker change was added. The only shared seam is the required provider-local admin client; the two small source classes preserve the system's existing semantic boundaries for activity and money.

## Review follow-up — nullable model and whitespace credentials

Review found two trust-boundary mismatches. The current official OpenAI Completions Usage reference, rechecked on 2026-08-25, explicitly shows `model: null` in its response example. The client instead required a nonblank string and discarded otherwise-valid token facts. Separately, the shared composition helper treated whitespace-only credentials as configured, enabling provider definitions and sources with a bogus bearer token.

TDD evidence:

- Client RED: the official-shape null-model response threw `InvalidDataException` at the required-string parser. GREEN: null is retained while a parameterized whitespace/non-string matrix remains rejected, `3/3`.
- Source RED: a nullable model reached `OpenAiUsageSource` but threw `NullReferenceException` in event-key encoding. GREEN: a real-PostgreSQL test stores the exact token lanes with `UsageEvent.Model = null` and `CostUsd = null`, while proving null and the real model name `"null"` have distinct stable keys, `1/1`.
- Composition RED: a whitespace-only `OPENAI_ADMIN_KEY` registered both OpenAI source implementations. GREEN: the shared `IsConfigured` root now uses `string.IsNullOrWhiteSpace` while retaining unresolved Key Vault rejection; both OpenAI definitions remain unconfigured and neither source resolves, `1/1`.

The nullable identity encoding uses a negative length sentinel, which cannot collide with any real string's nonnegative length prefix. The same existing helper now gives batch and service-tier nulls the same collision-safe treatment. No fallback model or price was introduced.

Fresh follow-up gates: combined OpenAI/client/source/host/worker `92/92`; full Release backend `843/843`; Release build succeeded with only the documented pre-existing `xUnit1025` warning; CSharpier 218 files and both scoped analyzer gates passed. No web, migration, package, or polling-worker code changed.
