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
        var usage = await afterCoverage.UsageEvents.AsNoTracking().SingleAsync(e => e.EventKey == "legacy-null-cost-a", ct);
        usage.SourceId.Should().Be(UsageSourceIds.LegacyApi);
        usage.SourceKind.Should().Be(SourceKind.Legacy);
        usage.UsageScope.Should().Be(UsageScope.Unknown);
        usage.CostBasis.Should().Be(CostBasis.Unknown);
        usage.ObservedAt.Should().Be(usage.IngestedAt);

        var aggregate = await afterCoverage.DailyAggregates.AsNoTracking().SingleAsync(
            a => a.Provider == Provider.OpenAI && a.Model == "unknown",
            ct
        );
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
        (await afterCoverage.UsageEvents.AsNoTracking().SingleAsync(e => e.EventKey == "legacy-null-cost-a", ct))
            .CostUsd.Should()
            .Be(0m);
    }
}
