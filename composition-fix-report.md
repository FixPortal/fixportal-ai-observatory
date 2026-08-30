# Composition review fix report — notification-settings-impl

## Finding 1+2 — Slack has no delivery fence, re-fires unbounded on email retry

**What changed:** Gave Slack its own idempotency fence, mirroring the existing
`EmailSentAt` pattern.

- `src/AiObservatory.Data/Entities/BudgetAlertClaim.cs` — added `Instant? SlackSentAt`.
- `src/AiObservatory.Data/Migrations/20260830134250_AddBudgetAlertClaimSlackSentAt.cs`
  (+ `.Designer.cs`, `AiObservatoryDbContextModelSnapshot.cs`) — EF migration adding the
  `SlackSentAt` column.
- `src/AiObservatory.Api/Services/IAlertNotifier.cs` — `BudgetAlertPayload` gained
  `Guid ClaimId`.
- `src/AiObservatory.Api/Services/BudgetAlertService.cs` — `DeliverEmailAsync` threads
  `email.ClaimId` into the payload.
- `src/AiObservatory.Data/Repositories/IUsageRepository.cs` /
  `src/AiObservatory.Data/Repositories/UsageRepository.cs` — added
  `GetBudgetAlertSlackSentAsync(claimId, ct)` and
  `MarkBudgetAlertSlackSentAsync(claimId, at, ct)`, same style as the neighboring
  budget-alert-email methods (single `ExecuteUpdateAsync`/`SingleOrDefaultAsync`, no
  transaction needed — single-column, single-row update).
- `src/AiObservatory.Api/Services/SlackAlertNotifier.cs` — added `IClock` to the
  constructor; after the existing "webhook not configured" no-op check, checks
  `GetBudgetAlertSlackSentAsync(payload.ClaimId, ct)` and no-ops if already true; after a
  successful POST, calls `MarkBudgetAlertSlackSentAsync(payload.ClaimId, clock.GetCurrentInstant(), ct)`.
  A failed POST still just logs and returns (unchanged best-effort behaviour) — no fence
  set, so a genuine delivery failure still gets one more attempt on the next email retry
  pass, exactly as it should.
- `src/AiObservatory.Api/Services/CompositeAlertNotifier.cs` — doc comment rewritten to
  describe the new fenced behaviour: Slack is attempted on every email lease-retry pass,
  but is a no-op after the first success per claim.

**Test updates (payload shape change, `ClaimId` threaded in):**
- `tests/AiObservatory.Api.Tests/Services/CompositeAlertNotifierTests.cs` — `MakePayload()`
  gets `Guid.NewGuid()`.
- `tests/AiObservatory.Api.Tests/Services/EmailAlertNotifierTests.cs` — same.
- `tests/AiObservatory.Api.Tests/Services/SlackAlertNotifierTests.cs` — `MakePayload()`
  uses a fixed `ClaimId` so the fence can be asserted against; `IUsageRepository`
  substitute now takes `IClock`; existing three tests stub
  `GetBudgetAlertSlackSentAsync(...) => false` (or omit it — false is NSubstitute's
  default for `Task<bool>` under `Substitute.For`, but it's stubbed explicitly where the
  test cares about reaching the POST). Two new tests added:
  - `NotifyAsync_does_not_post_when_slack_already_sent_for_this_claim` — stubs
    `GetBudgetAlertSlackSentAsync` true, asserts zero HTTP requests.
  - `NotifyAsync_marks_slack_sent_after_a_successful_post` — asserts
    `MarkBudgetAlertSlackSentAsync(ClaimId, now, ...)` received once after a 200.
- `tests/AiObservatory.Api.Tests/Services/SlackNotifierDiCompositionTests.cs` — the
  Program.cs DI-shape pin test needed `IClock` registered in its minimal
  `ServiceCollection` (it now fails to resolve `SlackAlertNotifier` otherwise — this was
  the one incidental regression the constructor change surfaced, fixed by adding
  `services.AddSingleton<IClock>(SystemClock.Instance)`).
- `tests/AiObservatory.Data.Tests/Repositories/UsageRepositoryTests.cs` — new test
  `Budget_alert_slack_sent_fence_starts_false_and_is_set_by_marking`, placed next to the
  existing `Budget_alert_claim_converges_concurrent_and_replayed_calls_on_one_durable_state`
  test in the same file/style (real Postgres, `[Trait("Category", "Integration")]`).
  Creates a claim via `GetOrCreateBudgetAlertAsync`, asserts
  `GetBudgetAlertSlackSentAsync` is false, calls `MarkBudgetAlertSlackSentAsync`, asserts
  it flips to true and the persisted `SlackSentAt` value round-trips.

**Covering tests:** all pass — see full suite output below.
`SlackAlertNotifierTests` (5 tests incl. 2 new), `CompositeAlertNotifierTests` (3),
`EmailAlertNotifierTests` (4), `SlackNotifierDiCompositionTests` (1),
`Budget_alert_slack_sent_fence_starts_false_and_is_set_by_marking` (Data.Tests, 1).

## Finding 3 — startup backfill can crash on concurrent-insert race

**What changed:** `src/AiObservatory.Api/Program.cs` — wrapped the backfill's
`SaveChangesAsync()` in the exact catch pattern from
`AdversarialReviewRepository.RecordRunAsync`
(`catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState:
PostgresErrorCodes.UniqueViolation })`). On catch: swallow and continue startup — this is
a one-time best-effort backfill, and someone else winning the insert race is exactly the
outcome it wants. Added `using Npgsql;`.

**Covering test:** no new dedicated unit test — this code path runs only at app startup,
before the DI container is fully up, and isn't independently unit-testable without
duplicating the whole host-startup harness. The identical pattern (`RecordRunAsync`) is
already covered by `AdversarialReviewRepositoryTests`, and the concurrent-insert race
mechanics for this exact table+constraint are proven by the Finding 4 integration test
below and by the pre-existing
`Budget_alert_claim_converges_concurrent_and_replayed_calls_on_one_durable_state` test
(same shape, different table). This is a smaller-scoped change than Finding 4 (best-effort
swallow vs. reload-and-reapply) so it's lower-risk; flagged here rather than silently
skipped.

## Finding 4 — PUT /notification-settings has the same race, causing 500 + lost update

**What changed:** `src/AiObservatory.Api/Endpoints/NotificationSettingsEndpoints.cs` —
extracted the partial-update field logic (the `TryGetProperty`-based email/Slack merge)
into a new `ApplyFields(settings, body, clock)` helper so it can be re-run after a reload.
The PUT handler now: tracks whether it's inserting a new row; wraps the insert path's
`SaveChangesAsync()` in the same `DbUpdateException`/`PostgresErrorCodes.UniqueViolation`
catch; on catch, detaches the losing entity, reloads the winner's row
(`db.NotificationSettings.SingleAsync(ct)`), reapplies `ApplyFields` with THIS request's
body on top of the winner's row, and saves again — a single reload-and-reapply, not a
retry loop. The update-only path (row already existed before this request) is unchanged.
Added `using Npgsql;`.

Note: this refactor also dropped the endpoint's Cognitive Complexity from 32 to 22
(S3776 warning, non-blocking) as a side effect of extracting `ApplyFields` — not a
deliberate scope item, just what fell out of doing the fix correctly.

**Covering test:**
`tests/AiObservatory.Api.IntegrationTests/NotificationSettingsEndpointsWafTests.cs` — new
test `Put_ConcurrentFirstWritesToAnEmptyTable_BothSucceedAndBothEditsSurvive`: clears the
table, fires two concurrent admin PUTs (one setting `alertEmailTo`, the other
`slackWebhookUrl`) via `Task.WhenAll` against the real `WebApplicationFactory` + Postgres,
asserts both responses are `200 OK` (neither 500s), asserts exactly one row exists, and
asserts the row carries both edits (deterministic because the two requests touch
different fields — the reload-and-reapply merges the loser's edit onto the winner's row
regardless of which wins, so this doesn't depend on which PUT actually won the race).
Verified against the real race: the raw Postgres log during the run shows the actual
23505 unique-violation on `PK_NotificationSettings` being thrown and caught — the
test isn't just passing by accident of ordering.

Ran with `--filter "NotificationSettings"` in isolation as well as inside the full suite;
passes in both.

## Full test-suite output

### AiObservatory.Api.Tests

```
Test run summary: Passed! - AiObservatory.Api.Tests.dll (net10.0|x64)
  total: 297
  failed: 0
  succeeded: 297
  skipped: 0
  duration: 2s 095ms
```

### AiObservatory.Data.Tests (real Postgres, TEST_DB_CONNECTION=127.0.0.1)

```
skipped AiObservatory.Data.Tests.Pricing.PricingRepricingServiceTests.QualificationRepricesEveryEligibleEventAndItsAggregate (0ms)
  Not run (due to explicit test filtering)   <- pre-existing, unrelated to this change

Test run summary: Passed! - AiObservatory.Data.Tests.dll (net10.0|x64)
  total: 191
  failed: 0
  succeeded: 190
  skipped: 1
  duration: 1m 04s 567ms
```

### AiObservatory.Api.IntegrationTests (real Postgres)

```
Test run summary: Failed! - AiObservatory.Api.IntegrationTests.dll (net10.0|x64)
  total: 186
  failed: 4
  succeeded: 182
  skipped: 0
```

Failures, all **pre-existing and unrelated** to this change:

- `EventsEndpointsWafTests.PatchEventCost_WhenSourceIdIsSupplied_UpdatesOnlyThatSourceIdentity`
- `EventsEndpointsWafTests.Legacy_post_and_patch_preserve_prefixed_keys_per_provider`
- `SourceStatusEndpointsWafTests.GetSourceStatus_ReturnsOrderedWireContractWithStoredErrorAndNullTimestamps`
- `SourceStatusEndpointsWafTests.GetSourceStatus_WhenUnauthenticated_ReturnsUnauthorized`

Verified pre-existing by `git stash`-ing every change in this pass, rebuilding against
the unmodified branch tip, and re-running: the same 4 tests fail on baseline, and all 4
pass in isolation when filtered to just those two classes (`--filter
"EventsEndpointsWafTests|SourceStatusEndpointsWafTests"` → 34/34 passed) on both baseline
and this branch. This is shared-mutable-DB test-order flakiness in the WAF integration
suite (`Insights` delete trigger interacting with fixture ordering across the shared
`ApiFactory` collection), not caused by anything touched in this pass — none of the four
findings touch Events, SourceStatus, or Insights-delete code paths.

All `NotificationSettings*` tests (12 total, including the new concurrent-PUT test) pass
both inside the full run and filtered in isolation
(`--filter "NotificationSettings"` → 12/12 passed).

## Scope discipline

No outbox/saga/distributed-transaction pattern added. No general retry/backoff — the
PUT's race recovery is a single reload-and-reapply, confirmed by reading the diff (no
loop construct). Email lease/retry mechanism (`EmailLeaseId`/`EmailLeaseAcquiredAt`/
`EmailSentAt`/`BudgetAlertEmailLease`) untouched beyond threading `ClaimId` through to
`BudgetAlertPayload`. Minor #9 (two independent `GetNotificationSettingsAsync` round
trips) not touched, per the standing ruling.
