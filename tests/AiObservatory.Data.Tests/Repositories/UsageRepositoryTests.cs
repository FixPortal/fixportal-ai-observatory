using AiObservatory.Data.Entities;
using AiObservatory.Data.Repositories;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace AiObservatory.Data.Tests.Repositories;

// Requires TEST_DB_CONNECTION env var pointing at a real PostgreSQL instance.
// Example: "Host=localhost;Database=aiobs_test;Username=postgres;Password=postgres"
public class UsageRepositoryTests : IAsyncLifetime
{
    private string _connStr = null!;
    private AiObservatoryDbContext _ctx = null!;
    private IUsageRepository _repo = null!;

    public async ValueTask InitializeAsync()
    {
        _connStr = Environment.GetEnvironmentVariable("TEST_DB_CONNECTION")
            ?? "Host=localhost;Database=aiobs_test;Username=postgres;Password=postgres";
        var options = new DbContextOptionsBuilder<AiObservatoryDbContext>()
            .UseNpgsql(_connStr, o => o.UseNodaTime())
            .Options;
        _ctx = new AiObservatoryDbContext(options);
        await _ctx.Database.MigrateAsync();
        _repo = new UsageRepository(_ctx);
    }

    public async ValueTask DisposeAsync()
    {
        if (_connStr.Contains("_test", StringComparison.OrdinalIgnoreCase))
        {
            await _ctx.Database.EnsureDeletedAsync();
        }
        await _ctx.DisposeAsync();
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
            CostUsd = 0.005m
        };

        await _repo.AddUsageEventAsync(evt, TestContext.Current.CancellationToken);

        var saved = await _ctx.UsageEvents.FindAsync([evt.Id], TestContext.Current.CancellationToken);
        saved.Should().NotBeNull();
        saved!.InputTokens.Should().Be(1000);
    }

    [Fact]
    public async Task RecordEvent_with_same_eventKey_records_and_aggregates_once()
    {
        static UsageEvent NewEvent() => new()
        {
            Provider = Provider.Copilot,
            OccurredAt = Instant.FromUtc(2026, 6, 2, 10, 0),
            IngestedAt = Instant.FromUtc(2026, 6, 2, 10, 0),
            Model = "gpt-5.4",
            InputTokens = 100,
            OutputTokens = 50,
            CostUsd = 0.01m,
            EventKey = "copilot:session-abc:gpt-5.4"
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
        static UsageEvent NewEvent() => new()
        {
            Provider = Provider.Anthropic,
            OccurredAt = Instant.FromUtc(2026, 6, 2, 10, 0),
            IngestedAt = Instant.FromUtc(2026, 6, 2, 10, 0),
            Model = "claude-sonnet-4-6",
            InputTokens = 100,
            OutputTokens = 50,
            CostUsd = 0.01m
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
            EventKey = "gemini:sess-abc:gemini-3.5-flash"
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

        var agg = await _ctx.DailyAggregates
            .FirstOrDefaultAsync(a => a.Model == "gemini-3.5-flash", ct);
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
            TestContext.Current.CancellationToken);

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
            EventKey = "gemini:sess-xyz:gemini-2.5-pro"
        };
        await _repo.RecordEventAsync(evt, ct);

        var result = await _repo.PatchEventCostAsync(Provider.Google, "gemini:sess-xyz:gemini-2.5-pro", 0.0083m, ct);

        result.Should().NotBeNull();
        result!.OldCostUsd.Should().Be(0.0083m);
        result.NewCostUsd.Should().Be(0.0083m);
        // CostUsd unchanged on the entity.
        (await _ctx.UsageEvents.FindAsync([evt.Id], ct))!.CostUsd.Should().Be(0.0083m);
    }

    [Fact]
    public async Task UpsertDailyAggregate_creates_then_replaces()
    {
        var date = new LocalDate(2026, 6, 1);

        await _repo.UpsertDailyAggregateAsync(date, Provider.Anthropic, "claude-sonnet-4-6",
            inputTokens: 1000, outputTokens: 500, cacheReadTokens: 100, cacheWriteTokens: 50, costUsd: 0.005m,
            ct: TestContext.Current.CancellationToken);

        await _repo.UpsertDailyAggregateAsync(date, Provider.Anthropic, "claude-sonnet-4-6",
            inputTokens: 2000, outputTokens: 800, cacheReadTokens: 200, cacheWriteTokens: 80, costUsd: 0.009m,
            ct: TestContext.Current.CancellationToken);

        var agg = await _ctx.DailyAggregates
            .FirstOrDefaultAsync(a => a.Date == date && a.Model == "claude-sonnet-4-6",
                TestContext.Current.CancellationToken);
        agg.Should().NotBeNull();
        agg!.InputTokens.Should().Be(2000);
        agg.CacheReadTokens.Should().Be(200);
        agg.CacheWriteTokens.Should().Be(80);
        agg.CostUsd.Should().Be(0.009m);
        agg.RequestCount.Should().Be(1);
    }
}
