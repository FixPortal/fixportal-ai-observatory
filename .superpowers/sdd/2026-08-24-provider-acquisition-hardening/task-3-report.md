# Task 3 report — Anthropic usage, billed costs, and Claude Code reports

## Delivery

- Base: `375e9e1e67e5dc5310d2e21a7d33b6051a3ce128`.
- Delivery commit: the commit containing this report; its SHA is recorded in the Task 3 handoff after commit creation.
- Scope: Task 3 only. `ProviderPollingWorkerService` was not edited. No package, migration, provider framework, or push was added.

One strict `AnthropicAdminClient` now completes and validates the three current Claude Platform Admin report flows before exposing any records. The reports remain three independent semantic sources: Messages usage becomes `Api/ListPriceEstimate`, the Platform Cost Report becomes retained `Api/Billed` observations and spend, and opt-in Claude Code activity becomes API or subscription usage with `ProviderEstimated` cost only when Anthropic supplies one.

## Official contract corrections

The wire contract was rechecked on 2026-08-25 against only current first-party Claude Platform documentation:

- Messages Usage: `https://platform.claude.com/docs/en/api/http/admin/usage_report/retrieve_messages`.
- Platform Cost Report: `https://platform.claude.com/docs/en/api/http/admin/cost_report/retrieve`.
- Claude Code Usage: `https://platform.claude.com/docs/en/api/http/admin/usage_report/retrieve_claude_code` and `https://platform.claude.com/docs/en/manage-claude/claude-code-analytics-api`.
- Standard error shape/taxonomy: `https://platform.claude.com/docs/en/api/errors`.

The implemented financial endpoint is `/v1/organizations/cost_report` under the Claude Platform Admin key. It is deliberately not Claude Enterprise `/v1/organizations/analytics/cost_report`, which uses a different Analytics key, entitlement, and schema. Platform cost amounts are decimal strings in fractional USD cents; they remain exact decimals in the client and are divided by 100 once in the billed source. Workspace, description, and the Platform response's parsed cost/model/context/geography/tier/token facts remain in raw evidence. Enterprise-only `list_amount` and product-surface fields were not invented.

Messages requests one complete inclusive range with daily buckets and exact model/service-tier/inference-geography/speed grouping. The client retains the uncached input, cache read, nested 5-minute/1-hour cache creation, and output lanes. The configured `fast-mode-2026-02-01` beta header is required for speed grouping.

Claude Code is a single-day endpoint, so the client completes every page for every requested UTC day before returning its immutable result. It validates the actor union, core and tool-action counts, customer type, remote/terminal facts, model token lanes, and optional estimated cost. The request date is `YYYY-MM-DD`; the response date is an RFC 3339 UTC-midnight timestamp. `estimated_cost.amount` is a JSON number in minor currency units and is divided by 100 only when persisted.

The standard Anthropic error taxonomy has no entitlement-specific error type separate from `permission_error`. The client therefore classifies an unavailable source only when a structured 403 contains `permission_error` plus an explicit unavailable/not-enabled/ineligible statement. Generic missing-scope 403s, authentication, timeouts, rate limits, and transient failures remain failures. Error bodies and credentials are never logged.

## Acquisition and source semantics

Every report uses a provider-local, exact 2 MiB per-response bound, a report-wide 10,000-page ceiling, unique nonblank advancing cursors, strict final null cursors, RFC 3339/range validation, exact UTC-midnight daily buckets, complete object/array validation, nonnegative count/token/money validation, and cancellation propagation. A malformed, failed, oversized, missing-cursor, repeated-cursor, or over-cap later page/day throws without returning a prefix, so no source writer has run.

`AnthropicUsageSource` groups only exact price-bearing bucket/model/tier/geography/speed dimensions, emits collision-safe correction keys, and writes atomically through `RecordEstimatedEventAsync`. Canonical raw pricing evidence carries exact cache-duration splits plus every provider record. Required grouped fields must remain present, but documented null values are retained rather than replaced; a null model or incomplete dimensions therefore retain usage with null central price.

`AnthropicCostsSource` maps stable bucket/workspace/description identities through the Task 1 `BillingObservationWriter` under `anthropic`/`api-usage`. Real PostgreSQL tests prove exact fractional-cent conversion, raw non-token facts, billed observation/spend provenance, zero retention, replay/no-op and correction convergence, no usage-aggregate mutation, and greatest upstream bucket end.

`ClaudeCodeUsageSource` groups one exact day/actor/customer/remote/terminal/model lane, sums duplicate upstream model rows, and uses a stable correction key. `customer_type` alone selects `Api` or `Subscription`. Complete upstream estimated costs become exact USD `ProviderEstimated` values; an absent estimate becomes `None` with null cost. These events use ordinary correction writes, never central list repricing or billed spend. Real PostgreSQL tests prove API/subscription separation, optional estimate semantics, token/cache lanes, correction/no-duplicate behavior, no spend rows, and latest completed usage day.

Composition always exposes exactly three Anthropic definitions. A configured `ANTHROPIC_BILLING_KEY` registers Messages and Costs plus one shared admin client. `CLAUDE_CODE_USAGE_ENABLED=true` registers the third source only with that key; flag-without-key fails startup clearly. Version, fast beta, integration User-Agent, whitespace/Key Vault credential gating, one client registration, and shared FX/writer resolution alongside unchanged OpenAI composition are covered. The README now documents the current Platform-vs-Enterprise key boundary, opt-in, AWS limitation, billed-vs-estimated separation, and local/remote double-counting risk without hardcoding stale plan eligibility.

## RED / GREEN

- Baseline before edits: Anthropic/OpenAI/host/worker `100/100`; Data writer/migration `24/24`.
- Initial RED: `AnthropicAdminClientTests` failed compilation because `AnthropicAdminClient` and `IAnthropicAdminClient` did not exist (`CS0246`). The RED suite already pinned all three official response shapes and failure boundaries.
- First GREEN: new client, three sources, real PostgreSQL money/Claude tests, and composition `58/58`.
- Strict grouped-shape RED: removing required nullable Messages or Cost grouping keys produced `0/6`; presence-with-null retention fixed it to `6/6`.
- Source-unavailability RED: explicit structured unavailability worked only for Claude Code (`1/3`); applying the same narrow classification to all three Admin reports produced `3/3` while generic permission errors remained failures.
- Review RED: shifted 24-hour Messages/Cost buckets were accepted (`0/2`), and Claude Code made a 10,001st request because its page budget reset each day (`0/1`). Exact UTC-midnight validation and one report-wide page budget produced `3/3`.
- Final expanded Anthropic/Claude/OpenAI/host/worker lane: `144/144`.
- Final Data writer/migration lane: `24/24`.

## Final gates

- `dotnet csharpier check .`: pass, 224 files.
- Release solution build: pass.
- Full Release backend: pass, `887/887`, zero failed and zero skipped.
- Scoped Ingest and Ingest-test analyzer gates: pass with no findings. The whole-solution analyzer gate still reports only the pre-existing `xUnit1025` at `SpendEntriesEndpointsWafTests.cs:813`.
- Observatory sweep Node regression: pass, `36/36`.
- Web Vitest: pass, `214/214` across 30 files.
- Web lint: pass with zero errors and seven pre-existing warnings.
- Web TypeScript/Vite production build: pass.
- `npm audit --audit-level=high`: pass, zero vulnerabilities.
- EF pending-model check: pass, no model changes; the existing EF tool `10.0.8` versus runtime `10.0.11` notice remains.
- NuGet vulnerable-package check: pass, no vulnerable direct or transitive packages in all seven projects.
- CI gate-coverage assertion: pass, all four jobs accounted for.
- Diff whitespace check: pass; only the existing working-copy README LF-to-CRLF notice remains.

## Ponytail full

The existing source, repository, pricing, billing-writer, HTTP, JSON, and DI patterns were reused. No universal provider client, runtime plugin system, cross-provider paginator, event bus, package, polling-worker branch, or speculative plan matrix was added. The only extracted methods are concrete Anthropic parsing and composition helpers required to keep analyzer gates clean.
