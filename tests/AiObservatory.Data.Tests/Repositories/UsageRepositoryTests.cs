using System.Text.Json;
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

        first.Disposition.Should().Be(RecordEventDisposition.Created);
        second.Disposition.Should().Be(RecordEventDisposition.Unchanged);
        first.IsDuplicate.Should().BeFalse();
        second.IsDuplicate.Should().BeTrue();
        second.EventId.Should().Be(first.EventId);

        (await _ctx.UsageEvents.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
        var agg = await _ctx.DailyAggregates.SingleAsync(TestContext.Current.CancellationToken);
        agg.InputTokens.Should().Be(100);
        agg.RequestCount.Should().Be(1);
    }

    [Theory]
    [InlineData(nameof(UsageEvent.Provider))]
    [InlineData(nameof(UsageEvent.OccurredAt))]
    [InlineData(nameof(UsageEvent.Model))]
    [InlineData(nameof(UsageEvent.InputTokens))]
    [InlineData(nameof(UsageEvent.OutputTokens))]
    [InlineData(nameof(UsageEvent.CacheReadTokens))]
    [InlineData(nameof(UsageEvent.CacheWriteTokens))]
    [InlineData(nameof(UsageEvent.CacheWrite1hTokens))]
    [InlineData(nameof(UsageEvent.ThoughtTokens))]
    [InlineData(nameof(UsageEvent.CostUsd))]
    [InlineData(nameof(UsageEvent.CacheSavingsUsd))]
    [InlineData(nameof(UsageEvent.Runtime))]
    [InlineData(nameof(UsageEvent.SessionId))]
    [InlineData(nameof(UsageEvent.AgentId))]
    [InlineData(nameof(UsageEvent.RawPayload))]
    [InlineData(nameof(UsageEvent.SourceKind))]
    [InlineData(nameof(UsageEvent.UsageScope))]
    [InlineData(nameof(UsageEvent.CostBasis))]
    public async Task RecordEvent_changed_canonical_field_replaces_snapshot(string changedField)
    {
        var ct = TestContext.Current.CancellationToken;
        var first = NewEvent();
        var corrected = NewEvent(changedField);

        await _repo.RecordEventAsync(first, ct);
        var correction = await _repo.RecordEventAsync(corrected, ct);
        var unchanged = await _repo.RecordEventAsync(NewEvent(changedField), ct);

        correction.Disposition.Should().Be(RecordEventDisposition.Corrected);
        unchanged.Disposition.Should().Be(RecordEventDisposition.Unchanged);
        correction.IsDuplicate.Should().BeFalse();
        unchanged.IsDuplicate.Should().BeTrue();
        correction.EventId.Should().Be(first.Id);
        unchanged.EventId.Should().Be(first.Id);

        var saved = await _ctx.UsageEvents.AsNoTracking().SingleAsync(ct);
        saved
            .Should()
            .BeEquivalentTo(
                corrected,
                options => options.Excluding(e => e.Id).Excluding(e => e.IngestedAt).Excluding(e => e.RawPayload)
            );
        using var savedPayload = JsonDocument.Parse(saved.RawPayload);
        using var expectedPayload = JsonDocument.Parse(corrected.RawPayload);
        JsonElement.DeepEquals(savedPayload.RootElement, expectedPayload.RootElement).Should().BeTrue();

        var rows = await _ctx.DailyAggregates.AsNoTracking().ToListAsync(ct);
        rows.Should().ContainSingle();
        rows.Sum(x => x.InputTokens).Should().Be(corrected.InputTokens);
        rows.Sum(x => x.OutputTokens).Should().Be(corrected.OutputTokens);
        rows.Sum(x => x.CacheReadTokens).Should().Be(corrected.CacheReadTokens ?? 0);
        rows.Sum(x => x.CacheWriteTokens).Should().Be(corrected.CacheWriteTokens ?? 0);
        rows.Sum(x => x.CacheWrite1hTokens).Should().Be(corrected.CacheWrite1hTokens ?? 0);
        rows.Sum(x => x.CostUsd).Should().Be(corrected.CostUsd ?? 0);
        rows.Sum(x => x.CacheSavingsUsd).Should().Be(corrected.CacheSavingsUsd ?? 0);
        rows.Sum(x => x.RequestCount).Should().Be(1);
    }

    [Fact]
    public async Task RecordEvent_correction_moves_bucket_and_removes_zero_row()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = NewEvent(model: "gpt-5.4", input: 100, cost: 1m);
        var corrected = NewEvent(model: "gpt-5.5", input: 175, cost: 2m);

        (await _repo.RecordEventAsync(first, ct)).Disposition.Should().Be(RecordEventDisposition.Created);
        (await _repo.RecordEventAsync(corrected, ct)).Disposition.Should().Be(RecordEventDisposition.Corrected);
        (await _repo.RecordEventAsync(NewEvent(model: "gpt-5.5", input: 175, cost: 2m), ct))
            .Disposition.Should()
            .Be(RecordEventDisposition.Unchanged);

        var rows = await _ctx.DailyAggregates.AsNoTracking().OrderBy(x => x.Model).ToListAsync(ct);
        rows.Should().NotContain(x => x.Model == "gpt-5.4");
        rows.Single(x => x.Model == "gpt-5.5").InputTokens.Should().Be(175);
        rows.Sum(x => x.CostUsd).Should().Be(2m);
    }

    [Fact]
    public async Task RecordEvent_correction_updates_unknown_cost_and_cache_savings_counts()
    {
        var ct = TestContext.Current.CancellationToken;

        await _repo.RecordEventAsync(NewEvent(cost: null, cacheSavings: null), ct);
        await _repo.RecordEventAsync(NewEvent(cost: 2m, cacheSavings: 0.5m), ct);

        var known = await _ctx.DailyAggregates.AsNoTracking().SingleAsync(ct);
        known.CostUsd.Should().Be(2m);
        known.UnknownCostCount.Should().Be(0);
        known.CacheSavingsUsd.Should().Be(0.5m);
        known.UnknownCacheSavingsCount.Should().Be(0);

        await _repo.RecordEventAsync(NewEvent(cost: null, cacheSavings: null), ct);

        var unknown = await _ctx.DailyAggregates.AsNoTracking().SingleAsync(ct);
        unknown.CostUsd.Should().Be(0m);
        unknown.UnknownCostCount.Should().Be(1);
        unknown.CacheSavingsUsd.Should().Be(0m);
        unknown.UnknownCacheSavingsCount.Should().Be(1);
    }

    [Fact]
    public async Task RecordEvent_new_cache_write_preserves_negative_savings_in_new_aggregate()
    {
        var ct = TestContext.Current.CancellationToken;

        await _repo.RecordEventAsync(NewEvent(cacheSavings: -0.75m), ct);

        var aggregate = await _ctx.DailyAggregates.AsNoTracking().SingleAsync(ct);
        aggregate.CacheSavingsUsd.Should().Be(-0.75m);
        aggregate.UnknownCacheSavingsCount.Should().Be(0);
    }

    [Fact]
    public async Task RecordEvent_changed_observation_metadata_is_unchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = NewEvent();
        var reread = NewEvent(
            ingestedAt: Instant.FromUtc(2026, 8, 24, 13, 0),
            observedAt: Instant.FromUtc(2026, 8, 24, 13, 1)
        );

        await _repo.RecordEventAsync(first, ct);
        var result = await _repo.RecordEventAsync(reread, ct);

        result.Disposition.Should().Be(RecordEventDisposition.Unchanged);
        result.IsDuplicate.Should().BeTrue();
        var saved = await _ctx.UsageEvents.AsNoTracking().SingleAsync(ct);
        saved.IngestedAt.Should().Be(first.IngestedAt);
        saved.ObservedAt.Should().Be(first.ObservedAt);
    }

    [Fact]
    public async Task RecordEvent_correction_copies_the_new_observed_at()
    {
        var ct = TestContext.Current.CancellationToken;
        var correctedObservedAt = Instant.FromUtc(2026, 8, 24, 13, 1);

        await _repo.RecordEventAsync(NewEvent(input: 100), ct);
        var result = await _repo.RecordEventAsync(NewEvent(input: 175, observedAt: correctedObservedAt), ct);

        result.Disposition.Should().Be(RecordEventDisposition.Corrected);
        var saved = await _ctx.UsageEvents.AsNoTracking().SingleAsync(ct);
        saved.ObservedAt.Should().Be(correctedObservedAt);
    }

    [Fact]
    public async Task RecordEvent_correction_refreshes_a_stale_tracked_snapshot()
    {
        var ct = TestContext.Current.CancellationToken;
        var options = new DbContextOptionsBuilder<AiObservatoryDbContext>()
            .UseNpgsql(_connStr, o => o.UseNodaTime())
            .Options;

        await _repo.RecordEventAsync(NewEvent(input: 100), ct);
        await using (var otherContext = new AiObservatoryDbContext(options))
        {
            var otherRepository = new UsageRepository(otherContext);
            (await otherRepository.RecordEventAsync(NewEvent(input: 200), ct))
                .Disposition.Should()
                .Be(RecordEventDisposition.Corrected);
            (await otherContext.DailyAggregates.AsNoTracking().SingleAsync(ct)).InputTokens.Should().Be(200);
        }

        await _repo.RecordEventAsync(NewEvent(input: 300), ct);

        _ctx.ChangeTracker.Clear();
        (await _ctx.UsageEvents.AsNoTracking().SingleAsync(ct)).InputTokens.Should().Be(300);
        var aggregate = await _ctx.DailyAggregates.AsNoTracking().SingleAsync(ct);
        aggregate.InputTokens.Should().Be(300);
        aggregate.RequestCount.Should().Be(1);
    }

    [Fact]
    public async Task RecordEvent_rejects_duplicate_raw_payload_properties()
    {
        var evt = NewEvent();
        evt.RawPayload = """{"request":"first","request":"last"}""";

        var act = () => _repo.RecordEventAsync(evt, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<JsonException>();
    }

    [Fact]
    public async Task RecordEvent_failed_correction_rolls_back_event_and_aggregate()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = NewEvent(input: 100, cost: 1m);
        await _repo.RecordEventAsync(first, ct);

        var act = () => _repo.RecordEventAsync(NewEvent(input: -1, cost: 2m), ct);

        await act.Should().ThrowAsync<DbUpdateException>();
        _ctx.ChangeTracker.Clear();
        var saved = await _ctx.UsageEvents.AsNoTracking().SingleAsync(ct);
        saved.InputTokens.Should().Be(100);
        saved.CostUsd.Should().Be(1m);
        var aggregate = await _ctx.DailyAggregates.AsNoTracking().SingleAsync(ct);
        aggregate.InputTokens.Should().Be(100);
        aggregate.CostUsd.Should().Be(1m);
        aggregate.RequestCount.Should().Be(1);
    }

    [Fact]
    public async Task RecordEvent_with_same_eventKey_and_different_sourceIds_records_both()
    {
        static UsageEvent NewEvent(string sourceId) =>
            new()
            {
                Provider = Provider.Copilot,
                OccurredAt = Instant.FromUtc(2026, 6, 2, 10, 0),
                IngestedAt = Instant.FromUtc(2026, 6, 2, 10, 0),
                Model = "gpt-5.4",
                InputTokens = 100,
                OutputTokens = 50,
                CostUsd = 0.01m,
                EventKey = "shared-session-key",
                SourceId = sourceId,
                SourceKind = SourceKind.LocalTelemetry,
                UsageScope = UsageScope.Subscription,
                CostBasis = CostBasis.Notional,
            };

        var first = await _repo.RecordEventAsync(
            NewEvent(UsageSourceIds.CopilotLocal),
            TestContext.Current.CancellationToken
        );
        var second = await _repo.RecordEventAsync(
            NewEvent(UsageSourceIds.GitHubBillingApi),
            TestContext.Current.CancellationToken
        );

        first.Disposition.Should().Be(RecordEventDisposition.Created);
        second.Disposition.Should().Be(RecordEventDisposition.Created);
        (await _ctx.UsageEvents.CountAsync(TestContext.Current.CancellationToken)).Should().Be(2);
        (await _ctx.DailyAggregates.CountAsync(TestContext.Current.CancellationToken)).Should().Be(2);
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

        first.Disposition.Should().Be(RecordEventDisposition.Created);
        second.Disposition.Should().Be(RecordEventDisposition.Created);
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
            command.CommandText = """LOCK TABLE "DailyAggregates" IN SHARE MODE""";
            await command.ExecuteNonQueryAsync(ct);
        }

        var first = firstRepository.RecordEventAsync(NewEvent(), ct);
        var second = secondRepository.RecordEventAsync(NewEvent(), ct);
        await WaitForBlockedWritesAsync(gateConnection, "DailyAggregates", ct);
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

    [Fact]
    public async Task ConcurrentRecordEvent_returns_its_source_scoped_winner_when_a_sibling_source_has_the_same_key()
    {
        var ct = TestContext.Current.CancellationToken;
        const string eventKey = "openai:2026-06-02:gpt-5.4";
        const string targetSourceId = UsageSourceIds.OpenAiUsageApi;
        var sibling = new UsageEvent
        {
            Provider = Provider.OpenAI,
            OccurredAt = Instant.FromUtc(2026, 6, 2, 10, 0),
            IngestedAt = Instant.FromUtc(2026, 6, 2, 10, 1),
            Model = "gpt-5.4",
            InputTokens = 100,
            OutputTokens = 50,
            CostUsd = 0.01m,
            EventKey = eventKey,
            SourceId = UsageSourceIds.OpenAiCostsApi,
        };
        await _repo.RecordEventAsync(sibling, ct);

        var options = new DbContextOptionsBuilder<AiObservatoryDbContext>()
            .UseNpgsql(_connStr, o => o.UseNodaTime())
            .Options;
        await using var firstContext = new AiObservatoryDbContext(options);
        await using var secondContext = new AiObservatoryDbContext(options);
        var firstRepository = new UsageRepository(firstContext);
        var secondRepository = new UsageRepository(secondContext);

        static UsageEvent NewTargetEvent() =>
            new()
            {
                Provider = Provider.OpenAI,
                OccurredAt = Instant.FromUtc(2026, 6, 2, 10, 0),
                IngestedAt = Instant.FromUtc(2026, 6, 2, 10, 1),
                Model = "gpt-5.4",
                InputTokens = 100,
                OutputTokens = 50,
                CostUsd = 0.01m,
                EventKey = eventKey,
                SourceId = targetSourceId,
            };

        await using var gateConnection = new NpgsqlConnection(_connStr);
        await gateConnection.OpenAsync(ct);
        await using var gateTransaction = await gateConnection.BeginTransactionAsync(ct);
        await using (var command = gateConnection.CreateCommand())
        {
            command.CommandText = """LOCK TABLE "DailyAggregates" IN SHARE MODE""";
            await command.ExecuteNonQueryAsync(ct);
        }

        var first = firstRepository.RecordEventAsync(NewTargetEvent(), ct);
        var second = secondRepository.RecordEventAsync(NewTargetEvent(), ct);
        await WaitForBlockedWritesAsync(gateConnection, "DailyAggregates", ct);
        await gateTransaction.CommitAsync(ct);

        var results = await Task.WhenAll(first, second);

        var targetId = results.Single(r => !r.IsDuplicate).EventId;
        results.Single(r => r.IsDuplicate).EventId.Should().Be(targetId);
        targetId.Should().NotBe(sibling.Id);
    }

    private static async Task WaitForBlockedWritesAsync(
        NpgsqlConnection connection,
        string tableName,
        CancellationToken ct
    )
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
                WHERE tables.relname = @tableName AND NOT locks.granted
                """;
            command.Parameters.AddWithValue("tableName", tableName);
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
        var result = await _repo.PatchEventCostAsync(
            Provider.Google,
            UsageSourceIds.LegacyApi,
            "gemini:sess-abc:gemini-3.5-flash",
            newCost,
            ct
        );

        result.Should().NotBeNull();
        result.OldCostUsd.Should().Be(0.0024m);
        result.NewCostUsd.Should().Be(newCost);

        // ExecuteUpdateAsync bypasses the EF change tracker; use AsNoTracking to read the live DB row.
        var saved = await _ctx.UsageEvents.AsNoTracking().FirstAsync(e => e.Id == evt.Id, ct);
        saved.CostUsd.Should().Be(newCost);

        var agg = await _ctx.DailyAggregates.FirstOrDefaultAsync(a => a.Model == "gemini-3.5-flash", ct);
        agg.Should().NotBeNull();
        // Aggregate delta = newCost - 0.0024m; check it's updated correctly.
        agg.CostUsd.Should().BeApproximately(newCost, precision: 0.000001m);
    }

    [Fact]
    public async Task PatchEventCost_returns_null_for_unknown_key()
    {
        var result = await _repo.PatchEventCostAsync(
            Provider.Google,
            UsageSourceIds.LegacyApi,
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

        var result = await _repo.PatchEventCostAsync(
            Provider.Google,
            UsageSourceIds.LegacyApi,
            "gemini:sess-xyz:gemini-2.5-pro",
            0.0083m,
            ct
        );

        result.Should().NotBeNull();
        result.OldCostUsd.Should().Be(0.0083m);
        result.NewCostUsd.Should().Be(0.0083m);
        // CostUsd unchanged on the entity.
        (await _ctx.UsageEvents.FindAsync([evt.Id], ct))!
            .CostUsd.Should()
            .Be(0.0083m);
    }

    [Fact]
    public async Task PatchEventCost_updates_only_the_exact_source()
    {
        var ct = TestContext.Current.CancellationToken;
        const string eventKey = "shared-cost-key";
        await _repo.RecordEventAsync(NewEvent(sourceId: UsageSourceIds.OpenAiUsageApi, eventKey: eventKey), ct);
        await _repo.RecordEventAsync(NewEvent(sourceId: UsageSourceIds.CodexLocal, eventKey: eventKey), ct);

        var result = await _repo.PatchEventCostAsync(Provider.OpenAI, UsageSourceIds.CodexLocal, eventKey, 3m, ct);

        result.Should().NotBeNull();
        var events = await _ctx.UsageEvents.AsNoTracking().OrderBy(x => x.SourceId).ToListAsync(ct);
        events.Single(x => x.SourceId == UsageSourceIds.CodexLocal).CostUsd.Should().Be(3m);
        events.Single(x => x.SourceId == UsageSourceIds.OpenAiUsageApi).CostUsd.Should().Be(1m);
        var rows = await _ctx.DailyAggregates.AsNoTracking().ToListAsync(ct);
        rows.Single(x => x.SourceId == UsageSourceIds.CodexLocal).CostUsd.Should().Be(3m);
        rows.Single(x => x.SourceId == UsageSourceIds.OpenAiUsageApi).CostUsd.Should().Be(1m);
    }

    [Fact]
    public async Task PatchEventCost_refreshes_a_stale_tracked_snapshot()
    {
        var ct = TestContext.Current.CancellationToken;
        const string eventKey = "stale-cost-key";
        var options = new DbContextOptionsBuilder<AiObservatoryDbContext>()
            .UseNpgsql(_connStr, o => o.UseNodaTime())
            .Options;

        await _repo.RecordEventAsync(NewEvent(eventKey: eventKey, cost: 1m), ct);
        await using (var otherContext = new AiObservatoryDbContext(options))
        {
            var otherRepository = new UsageRepository(otherContext);
            var otherResult = await otherRepository.PatchEventCostAsync(
                Provider.OpenAI,
                UsageSourceIds.OpenAiUsageApi,
                eventKey,
                2m,
                ct
            );
            otherResult.Should().NotBeNull();
            (await otherContext.DailyAggregates.AsNoTracking().SingleAsync(ct)).CostUsd.Should().Be(2m);
        }

        await _repo.PatchEventCostAsync(Provider.OpenAI, UsageSourceIds.OpenAiUsageApi, eventKey, 3m, ct);

        _ctx.ChangeTracker.Clear();
        (await _ctx.UsageEvents.AsNoTracking().SingleAsync(ct)).CostUsd.Should().Be(3m);
        var aggregate = await _ctx.DailyAggregates.AsNoTracking().SingleAsync(ct);
        aggregate.CostUsd.Should().Be(3m);
        aggregate.RequestCount.Should().Be(1);
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

    [Fact]
    public async Task GetBilledSpendGbpAsync_sums_signed_in_range_entries_and_filters_by_mapped_provider()
    {
        var ct = TestContext.Current.CancellationToken;
        var anthropic = await _ctx.SpendVendors.SingleAsync(v => v.Provider == Provider.Anthropic, ct);
        var openAi = await _ctx.SpendVendors.SingleAsync(v => v.Provider == Provider.OpenAI, ct);
        var unmapped = await _ctx.SpendVendors.SingleAsync(v => v.Key == "coderabbit", ct);
        var categoryId = await _ctx.SpendCategories.Select(c => c.Id).FirstAsync(ct);

        _ctx.SpendEntries.AddRange(
            Spend(anthropic.Id, categoryId, new LocalDate(2026, 8, 1), 20m),
            Spend(anthropic.Id, categoryId, new LocalDate(2026, 8, 2), -5m),
            Spend(openAi.Id, categoryId, new LocalDate(2026, 8, 2), 7m),
            Spend(unmapped.Id, categoryId, new LocalDate(2026, 8, 2), 3m),
            Spend(anthropic.Id, categoryId, new LocalDate(2026, 7, 31), 100m)
        );
        await _ctx.SaveChangesAsync(ct);

        (await _repo.GetBilledSpendGbpAsync(new LocalDate(2026, 8, 1), new LocalDate(2026, 8, 2), null, ct))
            .Should()
            .Be(25m);
        (
            await _repo.GetBilledSpendGbpAsync(
                new LocalDate(2026, 8, 1),
                new LocalDate(2026, 8, 2),
                Provider.Anthropic,
                ct
            )
        )
            .Should()
            .Be(15m);
    }

    [Fact]
    public async Task Budget_alert_claim_converges_concurrent_and_replayed_calls_on_one_durable_state()
    {
        var ct = TestContext.Current.CancellationToken;
        var rule = new BudgetRule { Period = BillingPeriod.Daily, ThresholdGbp = 10m };
        _ctx.BudgetRules.Add(rule);
        await _ctx.SaveChangesAsync(ct);

        var options = new DbContextOptionsBuilder<AiObservatoryDbContext>()
            .UseNpgsql(_connStr, o => o.UseNodaTime())
            .Options;
        await using var firstContext = new AiObservatoryDbContext(options);
        await using var secondContext = new AiObservatoryDbContext(options);
        var firstRepository = new UsageRepository(firstContext);
        var secondRepository = new UsageRepository(secondContext);
        var period = new LocalDate(2026, 8, 1);
        var triggeredAt = Instant.FromUtc(2026, 8, 2, 0, 5);

        var results = await Task.WhenAll(
            firstRepository.GetOrCreateBudgetAlertAsync(
                rule.Id,
                period,
                period,
                10m,
                15m,
                AlertInsight(period, triggeredAt),
                triggeredAt,
                ct
            ),
            secondRepository.GetOrCreateBudgetAlertAsync(
                rule.Id,
                period,
                period,
                10m,
                15m,
                AlertInsight(period, triggeredAt),
                triggeredAt,
                ct
            )
        );

        results.Should().ContainSingle(result => result.Created);
        results.Should().ContainSingle(result => !result.Created);
        (await _ctx.BudgetAlertClaims.AsNoTracking().CountAsync(ct)).Should().Be(1);
        (await _ctx.Insights.AsNoTracking().CountAsync(ct)).Should().Be(1);
        (await _ctx.BudgetRules.AsNoTracking().SingleAsync(candidate => candidate.Id == rule.Id, ct))
            .LastTriggeredAt.Should()
            .Be(triggeredAt);

        var replay = await firstRepository.GetOrCreateBudgetAlertAsync(
            rule.Id,
            period,
            period,
            99m,
            100m,
            AlertInsight(period, triggeredAt),
            triggeredAt,
            ct
        );
        replay.Created.Should().BeFalse();
        replay.ThresholdGbp.Should().Be(10m);
        replay.ActualSpendGbp.Should().Be(15m);
        (await _ctx.BudgetAlertClaims.AsNoTracking().CountAsync(ct)).Should().Be(1);
        (await _ctx.Insights.AsNoTracking().CountAsync(ct)).Should().Be(1);

        var pending = await firstRepository.GetPendingBudgetAlertEmailsAsync(ct);
        pending.Should().ContainSingle();
        pending[0].RuleId.Should().Be(rule.Id);
        pending[0].PeriodStart.Should().Be(period);
        pending[0].ActualSpendGbp.Should().Be(15m);

        var emailClaims = await Task.WhenAll(
            firstRepository.TryMarkBudgetAlertEmailAttemptedAsync(rule.Id, period, period, triggeredAt, ct),
            secondRepository.TryMarkBudgetAlertEmailAttemptedAsync(rule.Id, period, period, triggeredAt, ct)
        );
        emailClaims.Should().ContainSingle(claimed => claimed);
        emailClaims.Should().ContainSingle(claimed => !claimed);
        (await firstRepository.GetPendingBudgetAlertEmailsAsync(ct)).Should().BeEmpty();
    }

    private static Insight AlertInsight(LocalDate period, Instant generatedAt) =>
        new()
        {
            GeneratedAt = generatedAt,
            PeriodStart = period,
            PeriodEnd = period,
            InsightType = InsightType.BudgetAlert,
            Title = "Budget alert",
            Body = "Billed spend exceeded the threshold.",
        };

    private static SpendEntry Spend(Guid vendorId, Guid categoryId, LocalDate occurredOn, decimal amountGbp) =>
        new()
        {
            VendorId = vendorId,
            CategoryId = categoryId,
            OccurredOn = occurredOn,
            Amount = amountGbp,
            AmountGbp = amountGbp,
            Currency = "GBP",
            FxRate = 1m,
            Source = SpendSource.Manual,
            RecordedAt = Instant.FromUtc(2026, 8, 2, 12, 0),
            ObservedAt = Instant.FromUtc(2026, 8, 2, 12, 0),
            CostBasis = CostBasis.Billed,
        };

    private static UsageEvent NewEvent(
        string? changedField = null,
        string sourceId = UsageSourceIds.OpenAiUsageApi,
        string? eventKey = "day:model",
        string model = "gpt-5.4",
        long input = 100,
        decimal? cost = 1m,
        decimal? cacheSavings = 0.25m,
        Instant? ingestedAt = null,
        Instant? observedAt = null
    )
    {
        var evt = new UsageEvent
        {
            Provider = Provider.OpenAI,
            OccurredAt = Instant.FromUtc(2026, 8, 24, 12, 0),
            IngestedAt = ingestedAt ?? Instant.FromUtc(2026, 8, 24, 12, 1),
            Model = model,
            InputTokens = input,
            OutputTokens = 50,
            CacheReadTokens = 20,
            CacheWriteTokens = 20,
            CacheWrite1hTokens = 5,
            ThoughtTokens = 4,
            CostUsd = cost,
            CacheSavingsUsd = cacheSavings,
            Runtime = "api",
            SessionId = "session-1",
            AgentId = "agent-1",
            RawPayload = "{\"request\":\"stable\"}",
            SourceId = sourceId,
            SourceKind = SourceKind.ProviderApi,
            UsageScope = UsageScope.Api,
            CostBasis = CostBasis.ProviderEstimated,
            ObservedAt = observedAt ?? Instant.FromUtc(2026, 8, 24, 12, 2),
            EventKey = eventKey,
        };

        ChangeCanonicalField(evt, changedField);
        return evt;
    }

    private static void ChangeCanonicalField(UsageEvent evt, string? changedField)
    {
        switch (changedField)
        {
            case nameof(UsageEvent.Provider):
                evt.Provider = Provider.Anthropic;
                break;
            case nameof(UsageEvent.OccurredAt):
                evt.OccurredAt = Instant.FromUtc(2026, 8, 25, 12, 0);
                break;
            case nameof(UsageEvent.Model):
                evt.Model = "gpt-5.5";
                break;
            case nameof(UsageEvent.InputTokens):
                evt.InputTokens = 175;
                break;
            case nameof(UsageEvent.OutputTokens):
                evt.OutputTokens = 75;
                break;
            case nameof(UsageEvent.CacheReadTokens):
                evt.CacheReadTokens = 30;
                break;
            case nameof(UsageEvent.CacheWriteTokens):
                evt.CacheWriteTokens = 30;
                break;
            case nameof(UsageEvent.CacheWrite1hTokens):
                evt.CacheWrite1hTokens = 10;
                break;
            case nameof(UsageEvent.ThoughtTokens):
                evt.ThoughtTokens = 6;
                break;
            default:
                ChangeCanonicalEvidence(evt, changedField);
                break;
        }
    }

    private static void ChangeCanonicalEvidence(UsageEvent evt, string? changedField)
    {
        switch (changedField)
        {
            case nameof(UsageEvent.CostUsd):
                evt.CostUsd = 2m;
                break;
            case nameof(UsageEvent.CacheSavingsUsd):
                evt.CacheSavingsUsd = 0.5m;
                break;
            case nameof(UsageEvent.Runtime):
                evt.Runtime = "cloud";
                break;
            case nameof(UsageEvent.SessionId):
                evt.SessionId = "session-2";
                break;
            case nameof(UsageEvent.AgentId):
                evt.AgentId = "agent-2";
                break;
            case nameof(UsageEvent.RawPayload):
                evt.RawPayload = "{\"request\":\"corrected\"}";
                break;
            case nameof(UsageEvent.SourceKind):
                evt.SourceKind = SourceKind.LocalTelemetry;
                break;
            case nameof(UsageEvent.UsageScope):
                evt.UsageScope = UsageScope.Subscription;
                break;
            case nameof(UsageEvent.CostBasis):
                evt.CostBasis = CostBasis.Notional;
                break;
        }
    }
}
