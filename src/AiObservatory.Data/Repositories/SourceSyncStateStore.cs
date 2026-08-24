using AiObservatory.Data.Entities;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace AiObservatory.Data.Repositories;

public sealed class SourceSyncStateStore(AiObservatoryDbContext db)
{
    public async Task MarkUnconfiguredAsync(
        string sourceId,
        Duration expectedRefreshInterval,
        Instant current,
        CancellationToken cancellationToken
    )
    {
        _ = current;
        var state = await GetOrCreateAsync(sourceId, expectedRefreshInterval, cancellationToken);
        state.IsConfigured = false;
        state.IsAvailable = null;
        state.ConsecutiveFailureCount = 0;
        state.LastError = null;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkAttemptAsync(
        string sourceId,
        Duration expectedRefreshInterval,
        Instant current,
        CancellationToken cancellationToken
    )
    {
        var state = await GetOrCreateAsync(sourceId, expectedRefreshInterval, cancellationToken);
        state.IsConfigured = true;
        state.LastAttemptAt = current;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkSuccessAsync(
        string sourceId,
        Duration expectedRefreshInterval,
        Instant current,
        Instant? latestObservationAt,
        CancellationToken cancellationToken
    )
    {
        var state = await GetOrCreateAsync(sourceId, expectedRefreshInterval, cancellationToken);
        state.IsConfigured = true;
        state.IsAvailable = true;
        state.LastAttemptAt = current;
        state.LastSuccessAt = current;
        if (
            latestObservationAt is { } latest
            && (state.LatestObservationAt is null || latest > state.LatestObservationAt)
        )
        {
            state.LatestObservationAt = latest;
        }
        state.ConsecutiveFailureCount = 0;
        state.LastError = null;
        await db.SaveChangesAsync(cancellationToken);
    }

    public Task<int> MarkUnavailableAsync(
        string sourceId,
        Duration expectedRefreshInterval,
        Instant current,
        string error,
        CancellationToken cancellationToken
    ) => MarkFailedAttemptAsync(sourceId, expectedRefreshInterval, current, error, false, cancellationToken);

    public Task<int> MarkFailureAsync(
        string sourceId,
        Duration expectedRefreshInterval,
        Instant current,
        string error,
        CancellationToken cancellationToken
    ) => MarkFailedAttemptAsync(sourceId, expectedRefreshInterval, current, error, null, cancellationToken);

    private async Task<int> MarkFailedAttemptAsync(
        string sourceId,
        Duration expectedRefreshInterval,
        Instant current,
        string error,
        bool? isAvailable,
        CancellationToken cancellationToken
    )
    {
        var state = await GetOrCreateAsync(sourceId, expectedRefreshInterval, cancellationToken);
        state.IsConfigured = true;
        state.LastAttemptAt = current;
        if (isAvailable is not null)
        {
            state.IsAvailable = isAvailable;
        }
        state.ConsecutiveFailureCount++;
        state.LastError = error;
        await db.SaveChangesAsync(cancellationToken);
        return state.ConsecutiveFailureCount;
    }

    private async Task<SourceSyncState> GetOrCreateAsync(
        string sourceId,
        Duration expectedRefreshInterval,
        CancellationToken cancellationToken
    )
    {
        var state = await db.SourceSyncStates.SingleOrDefaultAsync(x => x.SourceId == sourceId, cancellationToken);
        if (state is null)
        {
            state = new SourceSyncState { SourceId = sourceId };
            db.SourceSyncStates.Add(state);
        }
        state.ExpectedRefreshIntervalSeconds = checked((long)expectedRefreshInterval.TotalSeconds);
        return state;
    }
}
