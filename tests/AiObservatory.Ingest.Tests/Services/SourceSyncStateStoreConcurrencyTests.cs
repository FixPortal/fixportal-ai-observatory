using AiObservatory.Data;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Repositories;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace AiObservatory.Ingest.Tests.Services;

[Collection("ProviderPollingWorker")]
public sealed class SourceSyncStateStoreConcurrencyTests(ProviderPollingDatabase database)
{
    [Fact]
    public async Task ConcurrentFirstSuccessesFromIndependentScopes_DoNotConflict()
    {
        await using var services = CreateServices();
        var sourceId = $"first-success-{Guid.NewGuid():N}";
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;
        var writes = Enumerable
            .Range(0, 16)
            .Select(async attempt =>
            {
                Interlocked.Increment(ref entered);
                await start.Task;
                await using var scope = services.CreateAsyncScope();
                await scope
                    .ServiceProvider.GetRequiredService<SourceSyncStateStore>()
                    .MarkSuccessAsync(
                        sourceId,
                        Duration.FromHours(1),
                        Instant.FromUtc(2026, 8, 24, 12, attempt),
                        Instant.FromUtc(2026, 8, 23, 12, attempt),
                        TestContext.Current.CancellationToken
                    );
            })
            .ToArray();

        entered.Should().Be(16);
        start.SetResult();
        await Task.WhenAll(writes);

        var state = await LoadStateAsync(services, sourceId);
        state.IsAvailable.Should().BeTrue();
        state.ConsecutiveFailureCount.Should().Be(0);
    }

    [Fact]
    public async Task ConcurrentSuccessesFromIndependentScopes_KeepGreatestObservationAndTimestamps()
    {
        await using var services = CreateServices();
        var sourceId = $"monotonic-success-{Guid.NewGuid():N}";
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;
        var writes = Enumerable
            .Range(0, 24)
            .Select(async attempt =>
            {
                Interlocked.Increment(ref entered);
                await start.Task;
                await using var scope = services.CreateAsyncScope();
                await scope
                    .ServiceProvider.GetRequiredService<SourceSyncStateStore>()
                    .MarkSuccessAsync(
                        sourceId,
                        Duration.FromHours(1),
                        Instant.FromUtc(2026, 8, 24, 12, attempt),
                        Instant.FromUtc(2026, 8, 23, 12, attempt),
                        TestContext.Current.CancellationToken
                    );
            })
            .ToArray();

        entered.Should().Be(24);
        start.SetResult();
        await Task.WhenAll(writes);

        var state = await LoadStateAsync(services, sourceId);
        state.LastAttemptAt.Should().Be(Instant.FromUtc(2026, 8, 24, 12, 23));
        state.LastSuccessAt.Should().Be(Instant.FromUtc(2026, 8, 24, 12, 23));
        state.LatestObservationAt.Should().Be(Instant.FromUtc(2026, 8, 23, 12, 23));
    }

    [Fact]
    public async Task ConcurrentFailuresFromIndependentScopes_IncrementWithoutLoss()
    {
        await using var services = CreateServices();
        var sourceId = $"concurrent-failure-{Guid.NewGuid():N}";
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;
        var writes = Enumerable
            .Range(0, 24)
            .Select(async attempt =>
            {
                Interlocked.Increment(ref entered);
                await start.Task;
                await using var scope = services.CreateAsyncScope();
                await scope
                    .ServiceProvider.GetRequiredService<SourceSyncStateStore>()
                    .MarkFailureAsync(
                        sourceId,
                        Duration.FromHours(1),
                        Instant.FromUtc(2026, 8, 24, 12, attempt),
                        $"failure {attempt}",
                        TestContext.Current.CancellationToken
                    );
            })
            .ToArray();

        entered.Should().Be(24);
        start.SetResult();
        await Task.WhenAll(writes);

        var state = await LoadStateAsync(services, sourceId);
        state.ConsecutiveFailureCount.Should().Be(24);
        state.LastAttemptAt.Should().Be(Instant.FromUtc(2026, 8, 24, 12, 23));
    }

    private ServiceProvider CreateServices()
    {
        var services = new ServiceCollection();
        services.AddDbContext<AiObservatoryDbContext>(options =>
            options.UseNpgsql(database.ConnectionString, npgsql => npgsql.UseNodaTime())
        );
        services.AddScoped<SourceSyncStateStore>();
        return services.BuildServiceProvider();
    }

    private static async Task<SourceSyncState> LoadStateAsync(ServiceProvider services, string sourceId)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope
            .ServiceProvider.GetRequiredService<AiObservatoryDbContext>()
            .SourceSyncStates.AsNoTracking()
            .SingleAsync(state => state.SourceId == sourceId, TestContext.Current.CancellationToken);
    }
}
