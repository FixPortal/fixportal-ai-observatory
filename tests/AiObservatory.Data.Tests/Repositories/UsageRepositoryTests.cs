using AiObservatory.Data.Entities;
using AiObservatory.Data.Repositories;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Npgsql;

namespace AiObservatory.Data.Tests.Repositories;

// Requires TEST_DB_CONNECTION env var pointing at a real PostgreSQL instance.
// Example: "Host=localhost;Database=aiobs_test;Username=postgres;Password=postgres"
[Trait("Category", "Integration")]
public class UsageRepositoryTests : IAsyncLifetime
{
    private string _connStr = null!;
    private AiObservatoryDbContext _ctx = null!;
    private IUsageRepository _repo = null!;

    public async ValueTask InitializeAsync()
    {
        var baseConn =
            Environment.GetEnvironmentVariable("TEST_DB_CONNECTION")
            ?? "Host=localhost;Database=aiobs_test;Username=postgres;Password=postgres";
        _connStr = new NpgsqlConnectionStringBuilder(baseConn)
        {
            Database = $"aiobs_test_usage_{Guid.NewGuid():N}",
        }.ConnectionString;
        var options = new DbContextOptionsBuilder<AiObservatoryDbContext>()
            .UseNpgsql(_connStr, o => o.UseNodaTime())
            .Options;
        _ctx = new AiObservatoryDbContext(options);
        await _ctx.Database.MigrateAsync();
        _repo = new UsageRepository(_ctx);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_ctx is not null && _connStr.Contains("_test", StringComparison.OrdinalIgnoreCase))
            {
                await _ctx.Database.EnsureDeletedAsync();
            }
        }
        finally
        {
            if (_ctx is not null)
            {
                await _ctx.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task AddUsageEvent_persists_record()
    {
        var evt = new UsageEvent
        {
            Provider = Provider.Anthropic,
            OccurredAt = Instant.FromUtc(2026, 6, 1, 10, 0),
            IngestedAt = Instant.FromUtc(2026, 6, 1, 11, 0),
            Model = "claude-sonnet-4-6",
            InputTokens = 1000,
            OutputTokens = 500,
            CostUsd = 0.005m,
        };

        await _repo.AddUsageEventAsync(evt, TestContext.Current.CancellationToken);

        var saved = await _ctx.UsageEvents.FindAsync([evt.Id], TestContext.Current.CancellationToken);
        saved.Should().NotBeNull();
        saved!.InputTokens.Should().Be(1000);
    }

    [Fact]
    public async Task RecordEvent_with_same_eventKey_records_and_aggregates_once()
    {
        static UsageEvent NewEvent() =>
            new()
            {
                Provider = Provider.Copilot,
                OccurredAt = Instant.FromUtc(2026, 6, 2, 10, 0),
                IngestedAt = Instant.FromUtc(2026, 6, 2, 10, 0),
                Model = "gpt-5.4",
                InputTokens = 100,
                OutputTokens = 50,
                CostUsd = 0.01m,
                EventKey = "copilot:session-abc:gpt-5.4",
            };

        var first = await _repo.RecordEventAsync(NewEvent(), TestContext.Current.CancellationToken);
        var second = await _repo.RecordEventAsync(NewEvent(), TestContext.Current.CancellationToken);

        first.IsDuplicate.Should().BeFalse();
        second.IsDuplicate.Should().BeTrue();
        second.EventId.Should().Be(first.EventId);

        (await _ctx.UsageEvents.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
        var agg = await _ctx.DailyAggregates.SingleAsync(TestContext.Current.CancellationToken);
        agg.InputTokens.Should().Be(100);
        agg.RequestCount.Should().Be(1);
    }

    [Fact]
    public async Task RecordEvent_without_eventKey_records_every_submission()
    {
        static UsageEvent NewEvent() =>
            new()
            {
                Provider = Provider.Anthropic,
                OccurredAt = Instant.FromUtc(2026, 6, 2, 10, 0),
                IngestedAt = Instant.FromUtc(2026, 6, 2, 10, 0),
                Model = "claude-sonnet-4-6",
                InputTokens = 100,
                OutputTokens = 50,
                CostUsd = 0.01m,
            };

        var first = await _repo.RecordEventAsync(NewEvent(), TestContext.Current.CancellationToken);
        var second = await _repo.RecordEventAsync(NewEvent(), TestContext.Current.CancellationToken);

        first.IsDuplicate.Should().BeFalse();
        second.IsDuplicate.Should().BeFalse();
        (await _ctx.UsageEvents.CountAsync(TestContext.Current.CancellationToken)).Should().Be(2);
        var agg = await _ctx.DailyAggregates.SingleAsync(TestContext.Current.CancellationToken);
        agg.RequestCount.Should().Be(2);
    }

    [Fact]
    public async Task ConcurrentRecordEventStoresAndAggregatesTheSharedKeyOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var options = new DbContextOptionsBuilder<AiObservatoryDbContext>()
            .UseNpgsql(_connStr, o => o.UseNodaTime())
            .Options;
        await using var firstContext = new AiObservatoryDbContext(options);
        await using var secondContext = new AiObservatoryDbContext(options);
        var firstRepository = new UsageRepository(firstContext);
        var secondRepository = new UsageRepository(secondContext);

        static UsageEvent NewEvent() =>
            new()
            {
                Provider = Provider.OpenAI,
                OccurredAt = Instant.FromUtc(2026, 6, 2, 10, 0),
                IngestedAt = Instant.FromUtc(2026, 6, 2, 10, 1),
                Model = "gpt-5.4",
                InputTokens = 100,
                OutputTokens = 50,
                CostUsd = 0.01m,
                EventKey = "openai:2026-06-02:gpt-5.4",
            };

        await using var gateConnection = new NpgsqlConnection(_connStr);
        await gateConnection.OpenAsync(ct);
        await using var gateTransaction = await gateConnection.BeginTransactionAsync(ct);
        await using (var command = gateConnection.CreateCommand())
        {
            command.CommandText = """LOCK TABLE "UsageEvents" IN SHARE MODE""";
            await command.ExecuteNonQueryAsync(ct);
        }

        var first = firstRepository.RecordEventAsync(NewEvent(), ct);
        var second = secondRepository.RecordEventAsync(NewEvent(), ct);
        await WaitForBlockedInsertsAsync(gateConnection, ct);
        await gateTransaction.CommitAsync(ct);

        var results = await Task.WhenAll(first, second);

        results.Should().ContainSingle(r => !r.IsDuplicate);
        results.Should().ContainSingle(r => r.IsDuplicate);
        results[0].EventId.Should().Be(results[1].EventId);
        (await _ctx.UsageEvents.AsNoTracking().CountAsync(ct)).Should().Be(1);
        var aggregate = await _ctx.DailyAggregates.AsNoTracking().SingleAsync(ct);
        aggregate.InputTokens.Should().Be(100);
        aggregate.RequestCount.Should().Be(1);
    }

    private static async Task WaitForBlockedInsertsAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));

        while (true)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT count(*)
                FROM pg_locks locks
                JOIN pg_class tables ON tables.oid = locks.relation
                WHERE tables.relname = 'UsageEvents' AND NOT locks.granted
                """;
            if (Convert.ToInt32(await command.ExecuteScalarAsync(timeout.Token)) == 2)
            {
                return;
            }

            await Task.Delay(10, timeout.Token);
        }
    }

    [Fact]
    public async Task PatchEventCost_updates_event_and_aggregate()
    {
        var ct = TestContext.Current.CancellationToken;

        // Record an event so both UsageEvents and DailyAggregates rows exist.
        var evt = new UsageEvent
        {
            Provider = Provider.Google,
            OccurredAt = Instant.FromUtc(2026, 6, 3, 9, 0),
            IngestedAt = Instant.FromUtc(2026, 6, 3, 9, 0),
            Model = "gemini-3.5-flash",
            InputTokens = 12000,
            OutputTokens = 800,
            CacheWriteTokens = 480,
            CostUsd = 0.0024m,
            EventKey = "gemini:sess-abc:gemini-3.5-flash",
        };
        await _repo.RecordEventAsync(evt, ct);

        // PATCH to the corrected cost.
        var newCost = 12000 * 1.50m / 1_000_000 + 800 * 9.00m / 1_000_000 + 480 * 3.50m / 1_000_000;
        var result = await _repo.PatchEventCostAsync(Provider.Google, "gemini:sess-abc:gemini-3.5-flash", newCost, ct);

        result.Should().NotBeNull();
        result!.OldCostUsd.Should().Be(0.0024m);
        result.NewCostUsd.Should().Be(newCost);

        // ExecuteUpdateAsync bypasses the EF change tracker; use AsNoTracking to read the live DB row.
        var saved = await _ctx.UsageEvents.AsNoTracking().FirstAsync(e => e.Id == evt.Id, ct);
        saved.CostUsd.Should().Be(newCost);

        var agg = await _ctx.DailyAggregates.FirstOrDefaultAsync(a => a.Model == "gemini-3.5-flash", ct);
        agg.Should().NotBeNull();
        // Aggregate delta = newCost - 0.0024m; check it's updated correctly.
        agg!.CostUsd.Should().BeApproximately(newCost, precision: 0.000001m);
    }

    [Fact]
    public async Task PatchEventCost_returns_null_for_unknown_key()
    {
        var result = await _repo.PatchEventCostAsync(
            Provider.Google,
            "gemini:nonexistent:model",
            0.01m,
            TestContext.Current.CancellationToken
        );

        result.Should().BeNull();
    }

    [Fact]
    public async Task PatchEventCost_noop_when_cost_unchanged()
    {
        var ct = TestContext.Current.CancellationToken;

        var evt = new UsageEvent
        {
            Provider = Provider.Google,
            OccurredAt = Instant.FromUtc(2026, 6, 4, 9, 0),
            IngestedAt = Instant.FromUtc(2026, 6, 4, 9, 0),
            Model = "gemini-2.5-pro",
            InputTokens = 5000,
            OutputTokens = 200,
            CostUsd = 0.0083m,
            EventKey = "gemini:sess-xyz:gemini-2.5-pro",
        };
        await _repo.RecordEventAsync(evt, ct);

        var result = await _repo.PatchEventCostAsync(Provider.Google, "gemini:sess-xyz:gemini-2.5-pro", 0.0083m, ct);

        result.Should().NotBeNull();
        result!.OldCostUsd.Should().Be(0.0083m);
        result.NewCostUsd.Should().Be(0.0083m);
        // CostUsd unchanged on the entity.
        (await _ctx.UsageEvents.FindAsync([evt.Id], ct))!
            .CostUsd.Should()
            .Be(0.0083m);
    }

    [Fact]
    public async Task UpsertDailyAggregate_creates_then_replaces()
    {
        var date = new LocalDate(2026, 6, 1);

        await _repo.UpsertDailyAggregateAsync(
            date,
            Provider.Anthropic,
            "claude-sonnet-4-6",
            inputTokens: 1000,
            outputTokens: 500,
            cacheReadTokens: 100,
            cacheWriteTokens: 50,
            costUsd: 0.005m,
            ct: TestContext.Current.CancellationToken
        );

        await _repo.UpsertDailyAggregateAsync(
            date,
            Provider.Anthropic,
            "claude-sonnet-4-6",
            inputTokens: 2000,
            outputTokens: 800,
            cacheReadTokens: 200,
            cacheWriteTokens: 80,
            costUsd: 0.009m,
            ct: TestContext.Current.CancellationToken
        );

        var agg = await _ctx.DailyAggregates.FirstOrDefaultAsync(
            a => a.Date == date && a.Model == "claude-sonnet-4-6",
            TestContext.Current.CancellationToken
        );
        agg.Should().NotBeNull();
        agg!.InputTokens.Should().Be(2000);
        agg.CacheReadTokens.Should().Be(200);
        agg.CacheWriteTokens.Should().Be(80);
        agg.CostUsd.Should().Be(0.009m);
        agg.RequestCount.Should().Be(1);
    }

    [Theory]
    [InlineData(nameof(DailyAggregate.InputTokens))]
    [InlineData(nameof(DailyAggregate.OutputTokens))]
    [InlineData(nameof(DailyAggregate.CacheReadTokens))]
    [InlineData(nameof(DailyAggregate.CacheWriteTokens))]
    [InlineData(nameof(DailyAggregate.CacheWrite1hTokens))]
    [InlineData(nameof(DailyAggregate.CostUsd))]
    [InlineData(nameof(DailyAggregate.RequestCount))]
    public async Task DailyAggregateRejectsNegativeNumericValues(string property)
    {
        var aggregate = new DailyAggregate
        {
            Date = new LocalDate(2026, 7, 30),
            Provider = Provider.Anthropic,
            Model = property,
            InputTokens = 1,
            OutputTokens = 1,
            CacheReadTokens = 1,
            CacheWriteTokens = 1,
            CacheWrite1hTokens = 1,
            CostUsd = 1m,
            RequestCount = 1,
        };
        switch (property)
        {
            case nameof(DailyAggregate.InputTokens):
                aggregate.InputTokens = -1;
                break;
            case nameof(DailyAggregate.OutputTokens):
                aggregate.OutputTokens = -1;
                break;
            case nameof(DailyAggregate.CacheReadTokens):
                aggregate.CacheReadTokens = -1;
                break;
            case nameof(DailyAggregate.CacheWriteTokens):
                aggregate.CacheWriteTokens = -1;
                break;
            case nameof(DailyAggregate.CacheWrite1hTokens):
                aggregate.CacheWrite1hTokens = -1;
                break;
            case nameof(DailyAggregate.CostUsd):
                aggregate.CostUsd = -1m;
                break;
            case nameof(DailyAggregate.RequestCount):
                aggregate.RequestCount = -1;
                break;
        }
        _ctx.DailyAggregates.Add(aggregate);

        var act = () => _ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task DailyAggregateRejectsOneHourCacheWritesAboveTotalCacheWrites()
    {
        _ctx.DailyAggregates.Add(
            new DailyAggregate
            {
                Date = new LocalDate(2026, 7, 30),
                Provider = Provider.Anthropic,
                Model = "invalid-cache-write-split",
                InputTokens = 1,
                OutputTokens = 1,
                CacheReadTokens = 1,
                CacheWriteTokens = 1,
                CacheWrite1hTokens = 2,
                CostUsd = 1m,
                RequestCount = 1,
            }
        );

        var act = () => _ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<DbUpdateException>();
    }
}
