using AiObservatory.Data.Entities;
using NodaTime;

namespace AiObservatory.Data.Repositories;

// Projection record properties are consumed by JSON serialization.
// ReSharper disable NotAccessedPositionalProperty.Global

/// <summary>
/// Outcome of <see cref="IUsageRepository.RecordEventAsync"/>: the id of the stored
/// event, or of the previously stored event when the submission was a duplicate.
/// </summary>
public sealed record RecordEventResult(Guid EventId, bool IsDuplicate);

/// <summary>
/// Outcome of <see cref="IUsageRepository.PurgeProviderAsync"/>: how many raw events
/// and pre-aggregated daily rows were removed for the provider.
/// </summary>
public sealed record PurgeResult(int DeletedEvents, int DeletedAggregates);

/// <summary>
/// Outcome of <see cref="IUsageRepository.PatchEventCostAsync"/>: the old and new cost
/// for the updated event. Null when no event with the given key exists.
/// </summary>
public sealed record PatchEventCostResult(Guid EventId, decimal OldCostUsd, decimal NewCostUsd);

/// <summary>
/// Minimal projection of a <see cref="AiObservatory.Data.Entities.UsageEvent"/> for cost-correction use.
/// </summary>
public sealed record EventCostRecord(
    Guid Id,
    string? EventKey,
    string? Model,
    long InputTokens,
    long OutputTokens,
    long? CacheWriteTokens,
    decimal CostUsd);

public interface IUsageRepository
{
    Task AddUsageEventAsync(UsageEvent evt, CancellationToken ct = default);

    Task<RecordEventResult> RecordEventAsync(UsageEvent evt, CancellationToken ct = default);

    /// <summary>
    /// Deletes ALL usage data for one provider — both the raw <c>UsageEvents</c> and the
    /// pre-aggregated <c>DailyAggregates</c> (which are maintained additively, so deleting
    /// events alone would leave the aggregates stale). Used to reset a provider before a
    /// clean backfill. Both deletes run in one transaction.
    /// </summary>
    Task<PurgeResult> PurgeProviderAsync(Provider provider, CancellationToken ct = default);

    Task UpsertDailyAggregateAsync(
        LocalDate date, Provider provider, string model,
        long inputTokens, long outputTokens, long cacheReadTokens, long cacheWriteTokens, decimal costUsd,
        int requestCount = 1, CancellationToken ct = default);

    Task<IReadOnlyList<DailyAggregate>> GetAggregatesAsync(
        LocalDate from, LocalDate to, CancellationToken ct = default);

    Task<IReadOnlyList<BudgetRule>> GetBudgetRulesAsync(CancellationToken ct = default);
    Task SetBudgetRuleTriggeredAsync(Guid ruleId, Instant triggeredAt, CancellationToken ct = default);

    Task AddInsightAsync(Insight insight, CancellationToken ct = default);
    Task<IReadOnlyList<Insight>> GetUnacknowledgedInsightsAsync(CancellationToken ct = default);
    Task AcknowledgeInsightAsync(Guid insightId, Instant at, CancellationToken ct = default);
    Task<LocalDate?> GetLatestInsightPeriodEndAsync(CancellationToken ct = default);

    Task<IReadOnlyList<Subscription>> GetActiveSubscriptionsAsync(LocalDate today, CancellationToken ct = default);

    /// <summary>
    /// Updates <c>CostUsd</c> on the event identified by <paramref name="provider"/> +
    /// <paramref name="eventKey"/> and adjusts the corresponding DailyAggregate by the same
    /// delta, atomically. Returns null when no event with that key exists.
    /// </summary>
    Task<PatchEventCostResult?> PatchEventCostAsync(Provider provider, string eventKey, decimal newCostUsd, CancellationToken ct = default);

    /// <summary>
    /// Returns raw events for <paramref name="provider"/>, ordered by <c>OccurredAt</c>,
    /// projected to the minimal fields needed for cost-correction backfill. Optionally
    /// scoped to an <c>OccurredAt</c> window (<paramref name="from"/>/<paramref name="to"/>),
    /// and always capped at <paramref name="limit"/> rows so a high-volume provider cannot
    /// be dumped unbounded in one request — page by date window for more.
    /// </summary>
    Task<IReadOnlyList<EventCostRecord>> GetEventsByProviderAsync(
        Provider provider, Instant? from = null, Instant? to = null, int limit = 10_000, CancellationToken ct = default);
}
