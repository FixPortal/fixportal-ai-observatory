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
        var baseConnection = Environment.GetEnvironmentVariable("TEST_DB_CONNECTION")
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
    public async Task AddUnknownCostCoverage_BackfillsLegacyNullCostsAndAllowsKnownZeroCorrection()
    {
        var ct = TestContext.Current.CancellationToken;
        await using (var beforeCoverage = new AiObservatoryDbContext(_options))
        {
            var migrator = beforeCoverage.Database.GetService<IMigrator>();
            await migrator.MigrateAsync("20260812022935_AddUsageEventTelemetryIdentity", ct);
            await beforeCoverage.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "UsageEvents" ("Id", "Provider", "OccurredAt", "IngestedAt", "Model", "InputTokens", "OutputTokens", "CostUsd", "RawPayload", "EventKey")
                VALUES ('10000000-0000-0000-0000-000000000001', 'OpenAI', '2026-08-12T00:30:00Z', '2026-08-12T00:30:00Z', NULL, 1, 1, NULL, '{{}}', 'legacy-null-cost')
                """, ct
            );
            await beforeCoverage.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "DailyAggregates" ("Date", "Provider", "Model", "InputTokens", "OutputTokens", "CacheReadTokens", "CacheWriteTokens", "CacheWrite1hTokens", "CostUsd", "RequestCount")
                VALUES ('2026-08-12', 'OpenAI', 'unknown', 1, 1, 0, 0, 0, 0, 1)
                """, ct
            );

            await migrator.MigrateAsync("20260812024132_AddUnknownCostCoverage", ct);
        }

        await using var afterCoverage = new AiObservatoryDbContext(_options);
        var aggregate = await afterCoverage.DailyAggregates.SingleAsync(TestContext.Current.CancellationToken);
        aggregate.UnknownCostCount.Should().Be(1);

        var repository = new UsageRepository(afterCoverage);
        var patch = await repository.PatchEventCostAsync(
            Provider.OpenAI,
            "legacy-null-cost",
            0m,
            TestContext.Current.CancellationToken
        );

        patch.Should().NotBeNull();
        aggregate = await afterCoverage.DailyAggregates.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        aggregate.UnknownCostCount.Should().Be(0);
        (await afterCoverage.UsageEvents.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken)).CostUsd.Should().Be(0m);
    }
}
