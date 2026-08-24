using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace AiObservatory.Data.Repositories;

public sealed class SourceSyncStateStore(AiObservatoryDbContext db)
{
    public Task MarkUnconfiguredAsync(
        string sourceId,
        Duration expectedRefreshInterval,
        Instant current,
        CancellationToken cancellationToken
    )
    {
        _ = current;
        var expectedSeconds = ExpectedSeconds(expectedRefreshInterval);
        return db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO "SourceSyncStates"
                ("SourceId", "IsConfigured", "IsAvailable", "ExpectedRefreshIntervalSeconds",
                 "LastAttemptAt", "LastSuccessAt", "LatestObservationAt", "ConsecutiveFailureCount", "LastError")
            VALUES
                ({sourceId}, FALSE, NULL, {expectedSeconds}, NULL, NULL, NULL, 0, NULL)
            ON CONFLICT ("SourceId") DO UPDATE SET
                "IsConfigured" = FALSE,
                "IsAvailable" = NULL,
                "ExpectedRefreshIntervalSeconds" = EXCLUDED."ExpectedRefreshIntervalSeconds",
                "ConsecutiveFailureCount" = 0,
                "LastError" = NULL
            """,
            cancellationToken
        );
    }

    public Task MarkAttemptAsync(
        string sourceId,
        Duration expectedRefreshInterval,
        Instant current,
        CancellationToken cancellationToken
    )
    {
        var expectedSeconds = ExpectedSeconds(expectedRefreshInterval);
        return db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO "SourceSyncStates"
                ("SourceId", "IsConfigured", "IsAvailable", "ExpectedRefreshIntervalSeconds",
                 "LastAttemptAt", "LastSuccessAt", "LatestObservationAt", "ConsecutiveFailureCount", "LastError")
            VALUES
                ({sourceId}, TRUE, NULL, {expectedSeconds}, {current}, NULL, NULL, 0, NULL)
            ON CONFLICT ("SourceId") DO UPDATE SET
                "IsConfigured" = TRUE,
                "ExpectedRefreshIntervalSeconds" = EXCLUDED."ExpectedRefreshIntervalSeconds",
                "LastAttemptAt" = GREATEST("SourceSyncStates"."LastAttemptAt", EXCLUDED."LastAttemptAt")
            """,
            cancellationToken
        );
    }

    public Task MarkSuccessAsync(
        string sourceId,
        Duration expectedRefreshInterval,
        Instant current,
        Instant? latestObservationAt,
        CancellationToken cancellationToken
    )
    {
        var expectedSeconds = ExpectedSeconds(expectedRefreshInterval);
        return db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO "SourceSyncStates"
                ("SourceId", "IsConfigured", "IsAvailable", "ExpectedRefreshIntervalSeconds",
                 "LastAttemptAt", "LastSuccessAt", "LatestObservationAt", "ConsecutiveFailureCount", "LastError")
            VALUES
                ({sourceId}, TRUE, TRUE, {expectedSeconds}, {current}, {current}, {latestObservationAt}, 0, NULL)
            ON CONFLICT ("SourceId") DO UPDATE SET
                "IsConfigured" = TRUE,
                "IsAvailable" = TRUE,
                "ExpectedRefreshIntervalSeconds" = EXCLUDED."ExpectedRefreshIntervalSeconds",
                "LastAttemptAt" = GREATEST("SourceSyncStates"."LastAttemptAt", EXCLUDED."LastAttemptAt"),
                "LastSuccessAt" = GREATEST("SourceSyncStates"."LastSuccessAt", EXCLUDED."LastSuccessAt"),
                "LatestObservationAt" = GREATEST(
                    "SourceSyncStates"."LatestObservationAt",
                    EXCLUDED."LatestObservationAt"
                ),
                "ConsecutiveFailureCount" = 0,
                "LastError" = NULL
            """,
            cancellationToken
        );
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
        var expectedSeconds = ExpectedSeconds(expectedRefreshInterval);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO "SourceSyncStates"
                ("SourceId", "IsConfigured", "IsAvailable", "ExpectedRefreshIntervalSeconds",
                 "LastAttemptAt", "LastSuccessAt", "LatestObservationAt", "ConsecutiveFailureCount", "LastError")
            VALUES
                ({sourceId}, TRUE, {isAvailable}, {expectedSeconds}, {current}, NULL, NULL, 1, {error})
            ON CONFLICT ("SourceId") DO UPDATE SET
                "IsConfigured" = TRUE,
                "IsAvailable" = COALESCE(EXCLUDED."IsAvailable", "SourceSyncStates"."IsAvailable"),
                "ExpectedRefreshIntervalSeconds" = EXCLUDED."ExpectedRefreshIntervalSeconds",
                "LastAttemptAt" = GREATEST("SourceSyncStates"."LastAttemptAt", EXCLUDED."LastAttemptAt"),
                "ConsecutiveFailureCount" = "SourceSyncStates"."ConsecutiveFailureCount" + 1,
                "LastError" = EXCLUDED."LastError"
            """,
            cancellationToken
        );

        return await db
            .SourceSyncStates.AsNoTracking()
            .Where(state => state.SourceId == sourceId)
            .Select(state => state.ConsecutiveFailureCount)
            .SingleAsync(cancellationToken);
    }

    private static long ExpectedSeconds(Duration expectedRefreshInterval) =>
        checked((long)expectedRefreshInterval.TotalSeconds);
}
