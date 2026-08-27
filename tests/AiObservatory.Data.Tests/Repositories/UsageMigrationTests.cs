using AiObservatory.Data.Entities;
using AiObservatory.Data.Repositories;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;
using Npgsql;

namespace AiObservatory.Data.Tests.Repositories;

[Trait("Category", "Integration")]
public class UsageMigrationTests : IAsyncLifetime
{
    private string _connectionString = null!;
    private DbContextOptions<AiObservatoryDbContext> _options = null!;

    public ValueTask InitializeAsync()
    {
        var baseConnection =
            Environment.GetEnvironmentVariable("TEST_DB_CONNECTION")
            ?? "Host=localhost;Database=aiobs_test;Username=postgres;Password=postgres";
        _connectionString = new NpgsqlConnectionStringBuilder(baseConnection)
        {
            Database = $"aiobs_test_migration_{Guid.NewGuid():N}",
        }.ConnectionString;
        _options = new DbContextOptionsBuilder<AiObservatoryDbContext>()
            .UseNpgsql(_connectionString, options => options.UseNodaTime())
            .Options;
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await using var db = new AiObservatoryDbContext(_options);
        await db.Database.EnsureDeletedAsync();
    }

    [Theory]
    [InlineData(SubscriptionBillingInterval.Annual, 13)]
    [InlineData(SubscriptionBillingInterval.Annual, null)]
    [InlineData(SubscriptionBillingInterval.Monthly, 7)]
    public async Task SubscriptionBillingMigration_RejectsInvalidIntervalMonthPairs(
        SubscriptionBillingInterval interval,
        int? billingMonth
    )
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = new AiObservatoryDbContext(_options);
        await db.Database.MigrateAsync(ct);
        db.Subscriptions.Add(
            new Subscription
            {
                Provider = Provider.Google,
                Name = "Invalid subscription",
                CostAmount = 1m,
                Currency = "GBP",
                BillingInterval = interval,
                BillingMonth = billingMonth,
                BillingDay = 1,
                ActiveFrom = new LocalDate(2026, 1, 1),
            }
        );

        var save = () => db.SaveChangesAsync(ct);

        await save.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task AddUnknownCostCoverage_BackfillsGroupedLegacyNullCostsAndAllowsKnownZeroCorrection()
    {
        var ct = TestContext.Current.CancellationToken;
        await using (var beforeCoverage = new AiObservatoryDbContext(_options))
        {
            var migrator = beforeCoverage.Database.GetService<IMigrator>();
            await migrator.MigrateAsync("20260812022935_AddUsageEventTelemetryIdentity", ct);
            const string rawPayload = "{}";
            await beforeCoverage.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO "UsageEvents" ("Id", "Provider", "OccurredAt", "IngestedAt", "Model", "InputTokens", "OutputTokens", "CostUsd", "RawPayload", "EventKey")
                VALUES ('10000000-0000-0000-0000-000000000001', 'OpenAI', '2026-08-12T00:30:00Z', '2026-08-12T00:30:00Z', NULL, 1, 1, NULL, CAST({rawPayload} AS jsonb), 'legacy-null-cost-a')
                """,
                ct
            );
            await beforeCoverage.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO "UsageEvents" ("Id", "Provider", "OccurredAt", "IngestedAt", "Model", "InputTokens", "OutputTokens", "CostUsd", "RawPayload", "EventKey")
                VALUES ('10000000-0000-0000-0000-000000000002', 'OpenAI', '2026-08-12T23:30:00Z', '2026-08-12T23:30:00Z', NULL, 1, 1, NULL, CAST({rawPayload} AS jsonb), 'legacy-null-cost-b')
                """,
                ct
            );
            await beforeCoverage.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO "UsageEvents" ("Id", "Provider", "OccurredAt", "IngestedAt", "Model", "InputTokens", "OutputTokens", "CostUsd", "RawPayload", "EventKey")
                VALUES ('10000000-0000-0000-0000-000000000003', 'Google', '2026-08-13T00:30:00Z', '2026-08-13T00:30:00Z', 'control', 1, 1, NULL, CAST({rawPayload} AS jsonb), 'legacy-null-cost-control')
                """,
                ct
            );
            await beforeCoverage.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "DailyAggregates" ("Date", "Provider", "Model", "InputTokens", "OutputTokens", "CacheReadTokens", "CacheWriteTokens", "CacheWrite1hTokens", "CostUsd", "RequestCount")
                VALUES ('2026-08-12', 'OpenAI', 'unknown', 2, 2, 0, 0, 0, 0, 2)
                """,
                ct
            );
            await beforeCoverage.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "DailyAggregates" ("Date", "Provider", "Model", "InputTokens", "OutputTokens", "CacheReadTokens", "CacheWriteTokens", "CacheWrite1hTokens", "CostUsd", "RequestCount")
                VALUES ('2026-08-13', 'Google', 'control', 1, 1, 0, 0, 0, 0, 1)
                """,
                ct
            );

            await migrator.MigrateAsync(cancellationToken: ct);
        }

        await using var afterCoverage = new AiObservatoryDbContext(_options);
        var aggregates = await afterCoverage.DailyAggregates.ToListAsync(ct);
        var usage = await afterCoverage
            .UsageEvents.AsNoTracking()
            .SingleAsync(e => e.EventKey == "OpenAI:legacy-null-cost-a", ct);
        usage.SourceId.Should().Be(UsageSourceIds.LegacyApi);
        usage.SourceKind.Should().Be(SourceKind.Legacy);
        usage.UsageScope.Should().Be(UsageScope.Unknown);
        usage.CostBasis.Should().Be(CostBasis.Unknown);
        usage.ObservedAt.Should().Be(usage.IngestedAt);

        var aggregate = await afterCoverage
            .DailyAggregates.AsNoTracking()
            .SingleAsync(a => a.Provider == Provider.OpenAI && a.Model == "unknown", ct);
        aggregate.SourceId.Should().Be(UsageSourceIds.LegacyApi);
        aggregate.SourceKind.Should().Be(SourceKind.Legacy);
        aggregate.UsageScope.Should().Be(UsageScope.Unknown);
        aggregate.CostBasis.Should().Be(CostBasis.Unknown);

        aggregates
            .Should()
            .ContainSingle(a => a.Provider == Provider.OpenAI && a.Model == "unknown")
            .Which.UnknownCostCount.Should()
            .Be(2);
        aggregates
            .Should()
            .ContainSingle(a => a.Provider == Provider.Google && a.Model == "control")
            .Which.UnknownCostCount.Should()
            .Be(1);

        var repository = new UsageRepository(afterCoverage);
        var patch = await repository.PatchEventCostAsync(
            Provider.OpenAI,
            UsageSourceIds.LegacyApi,
            "legacy-null-cost-a",
            0m,
            TestContext.Current.CancellationToken
        );

        patch.Should().NotBeNull();
        aggregates = await afterCoverage.DailyAggregates.AsNoTracking().ToListAsync(ct);
        aggregates
            .Should()
            .ContainSingle(a => a.Provider == Provider.OpenAI && a.Model == "unknown")
            .Which.UnknownCostCount.Should()
            .Be(1);
        aggregates
            .Should()
            .ContainSingle(a => a.Provider == Provider.Google && a.Model == "control")
            .Which.UnknownCostCount.Should()
            .Be(1);
        (await afterCoverage.UsageEvents.AsNoTracking().SingleAsync(e => e.EventKey == "OpenAI:legacy-null-cost-a", ct))
            .CostUsd.Should()
            .Be(0m);
    }

    [Fact]
    public async Task AddObservationProvenance_preserves_same_legacy_key_from_different_providers()
    {
        var ct = TestContext.Current.CancellationToken;
        await using (var beforeProvenance = new AiObservatoryDbContext(_options))
        {
            var migrator = beforeProvenance.Database.GetService<IMigrator>();
            await migrator.MigrateAsync("20260812024132_AddUnknownCostCoverage", ct);
            const string rawPayload = "{}";
            const string legacyKey = "x";
            const string prefixedLegacyKey = "OpenAI:x";
            await beforeProvenance.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO "UsageEvents" ("Id", "Provider", "OccurredAt", "IngestedAt", "Model", "InputTokens", "OutputTokens", "CostUsd", "RawPayload", "EventKey")
                VALUES ('20000000-0000-0000-0000-000000000001', 'OpenAI', '2026-08-12T00:30:00Z', '2026-08-12T00:30:00Z', 'gpt-5.4', 1, 1, 1, CAST({rawPayload} AS jsonb), {legacyKey})
                """,
                ct
            );
            await beforeProvenance.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO "UsageEvents" ("Id", "Provider", "OccurredAt", "IngestedAt", "Model", "InputTokens", "OutputTokens", "CostUsd", "RawPayload", "EventKey")
                VALUES ('20000000-0000-0000-0000-000000000002', 'OpenAI', '2026-08-12T00:30:00Z', '2026-08-12T00:30:00Z', 'gpt-5.4', 1, 1, 1, CAST({rawPayload} AS jsonb), {prefixedLegacyKey})
                """,
                ct
            );
            await beforeProvenance.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO "UsageEvents" ("Id", "Provider", "OccurredAt", "IngestedAt", "Model", "InputTokens", "OutputTokens", "CostUsd", "RawPayload", "EventKey")
                VALUES ('20000000-0000-0000-0000-000000000003', 'Google', '2026-08-12T00:30:00Z', '2026-08-12T00:30:00Z', 'gemini-2.5-pro', 1, 1, 1, CAST({rawPayload} AS jsonb), {legacyKey})
                """,
                ct
            );
            await beforeProvenance.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO "UsageEvents" ("Id", "Provider", "OccurredAt", "IngestedAt", "Model", "InputTokens", "OutputTokens", "CostUsd", "RawPayload", "EventKey")
                VALUES ('20000000-0000-0000-0000-000000000004', 'Google', '2026-08-12T00:30:00Z', '2026-08-12T00:30:00Z', 'gemini-2.5-pro', 1, 1, 1, CAST({rawPayload} AS jsonb), {prefixedLegacyKey})
                """,
                ct
            );

            await migrator.MigrateAsync("20260824172007_AddObservationProvenance", ct);
        }

        await using var afterProvenance = new AiObservatoryDbContext(_options);
        var events = await afterProvenance.UsageEvents.AsNoTracking().OrderBy(e => e.Provider).ToListAsync(ct);

        events.Should().HaveCount(4);
        events
            .Select(e => e.EventKey)
            .Should()
            .BeEquivalentTo("OpenAI:x", "OpenAI:OpenAI:x", "Google:x", "Google:OpenAI:x");

        var rollbackMigrator = afterProvenance.Database.GetService<IMigrator>();
        await rollbackMigrator.MigrateAsync("20260812024132_AddUnknownCostCoverage", ct);
        var restoredKeys = await afterProvenance
            .Database.SqlQueryRaw<string>("SELECT \"EventKey\" AS \"Value\" FROM \"UsageEvents\" ORDER BY \"Provider\"")
            .ToListAsync(ct);

        restoredKeys.Should().BeEquivalentTo("x", "OpenAI:x", "x", "OpenAI:x");
    }

    [Fact]
    public async Task AddBudgetAlertsAndRenameThresholdToGbp_is_one_value_preserving_deployment_boundary()
    {
        var ct = TestContext.Current.CancellationToken;
        var existingRuleId = Guid.Parse("30000000-0000-0000-0000-000000000001");
        var newRuleId = Guid.Parse("30000000-0000-0000-0000-000000000002");
        await using (var beforeMigration = new AiObservatoryDbContext(_options))
        {
            var migrator = beforeMigration.Database.GetService<IMigrator>();
            await migrator.MigrateAsync("20260825220510_TrackPendingSourceWindows", ct);
            await beforeMigration.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO "BudgetRules" ("Id", "Period", "ThresholdUsd")
                VALUES ({existingRuleId}, 'Daily', 123.45)
                """,
                ct
            );
            await migrator.MigrateAsync(cancellationToken: ct);
        }

        await using var afterMigration = new AiObservatoryDbContext(_options);
        var databaseUtcDate = await afterMigration
            .Database.SqlQueryRaw<LocalDate>("SELECT (CURRENT_TIMESTAMP AT TIME ZONE 'UTC')::date AS \"Value\"")
            .SingleAsync(ct);
        var existingRule = await afterMigration
            .BudgetRules.AsNoTracking()
            .SingleAsync(rule => rule.Id == existingRuleId, ct);
        existingRule.ThresholdGbp.Should().Be(123.45m);
        existingRule.EvaluationStartsOn.Should().Be(databaseUtcDate);

        await afterMigration.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO "BudgetRules" ("Id", "Period", "ThresholdGbp")
            VALUES ({newRuleId}, 'Daily', 10)
            """,
            ct
        );
        var newRule = await afterMigration.BudgetRules.AsNoTracking().SingleAsync(rule => rule.Id == newRuleId, ct);
        newRule.EvaluationStartsOn.Should().Be(databaseUtcDate);

        var claimConstraints = await afterMigration
            .Database.SqlQueryRaw<string>(
                """
                SELECT conname AS "Value"
                FROM pg_constraint
                WHERE conrelid = '"BudgetAlertClaims"'::regclass
                  AND conname IN ('CK_BudgetAlertClaim_EmailLease', 'CK_BudgetAlertClaim_Period')
                ORDER BY conname
                """
            )
            .ToListAsync(ct);
        claimConstraints.Should().BeEquivalentTo("CK_BudgetAlertClaim_EmailLease", "CK_BudgetAlertClaim_Period");

        var rollbackMigrator = afterMigration.Database.GetService<IMigrator>();
        await rollbackMigrator.MigrateAsync("20260825220510_TrackPendingSourceWindows", ct);
        var restoredThreshold = await afterMigration
            .Database.SqlQuery<decimal>(
                $"""SELECT "ThresholdUsd" AS "Value" FROM "BudgetRules" WHERE "Id" = {existingRuleId}"""
            )
            .SingleAsync(ct);
        var removedObjects = await afterMigration
            .Database.SqlQueryRaw<string>(
                """
                SELECT table_name AS "Value"
                FROM information_schema.tables
                WHERE table_schema = 'public' AND table_name = 'BudgetAlertClaims'
                UNION ALL
                SELECT column_name AS "Value"
                FROM information_schema.columns
                WHERE table_schema = 'public'
                  AND table_name = 'BudgetRules'
                  AND column_name = 'EvaluationStartsOn'
                """
            )
            .ToListAsync(ct);

        restoredThreshold.Should().Be(123.45m);
        removedObjects.Should().BeEmpty();
    }
}
