using System.Data;
using AiObservatory.Data.Entities;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Npgsql;

namespace AiObservatory.Data.Repositories;

public class UsageRepository(AiObservatoryDbContext ctx) : IUsageRepository
{
    public async Task AddUsageEventAsync(UsageEvent evt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (evt.ObservedAt == default)
        {
            evt.ObservedAt = evt.IngestedAt;
        }
        ctx.UsageEvents.Add(evt);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<RecordEventResult> RecordEventAsync(UsageEvent evt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (evt.ObservedAt == default)
        {
            evt.ObservedAt = evt.IngestedAt;
        }
        var providerStr = evt.Provider.ToString();
        var model = evt.Model ?? "unknown";
        var date = evt.OccurredAt.InUtc().Date;
        var sourceKind = evt.SourceKind.ToString();
        var usageScope = evt.UsageScope.ToString();
        var costBasis = evt.CostBasis.ToString();

        if (evt.EventKey is not null)
        {
            var existingId = await ctx
                .UsageEvents.AsNoTracking()
                .Where(e => e.SourceId == evt.SourceId && e.EventKey == evt.EventKey)
                .Select(e => (Guid?)e.Id)
                .FirstOrDefaultAsync(ct);
            if (existingId is not null)
            {
                return new RecordEventResult(existingId.Value, IsDuplicate: true);
            }
        }

        await using var tx = await ctx.Database.BeginTransactionAsync(ct);
        ctx.UsageEvents.Add(evt);
        try
        {
            await ctx.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
            when (evt.EventKey is not null
                && ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation }
            )
        {
            // Lost a concurrent race on the EventKey unique index: another request
            // recorded (and aggregated) this event between the pre-check and the insert.
            await tx.RollbackAsync(ct);
            ctx.Entry(evt).State = EntityState.Detached;
            var winnerId = await ctx
                .UsageEvents.AsNoTracking()
                .Where(e => e.Provider == evt.Provider && e.EventKey == evt.EventKey)
                .Select(e => e.Id)
                .FirstAsync(ct);
            return new RecordEventResult(winnerId, IsDuplicate: true);
        }
        var cacheRead = evt.CacheReadTokens ?? 0L;
        var cacheWrite = evt.CacheWriteTokens ?? 0L;
        var cacheWrite1H = evt.CacheWrite1hTokens ?? 0L;
        var unknownCostCount = evt.CostUsd is null ? 1 : 0;
        var knownCostUsd = evt.CostUsd ?? 0m;
        var unknownCacheSavingsCount = evt.CacheSavingsUsd is null ? 1 : 0;
        var cacheSavingsUsd = evt.CacheSavingsUsd ?? 0m;
        await ctx.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO "DailyAggregates" ("Date", "Provider", "Model", "SourceId", "SourceKind", "UsageScope", "CostBasis", "InputTokens", "OutputTokens", "CacheReadTokens", "CacheWriteTokens", "CacheWrite1hTokens", "CostUsd", "UnknownCostCount", "CacheSavingsUsd", "UnknownCacheSavingsCount", "RequestCount")
            VALUES ({date}, {providerStr}, {model}, {evt.SourceId}, {sourceKind}, {usageScope}, {costBasis}, {evt.InputTokens}, {evt.OutputTokens}, {cacheRead}, {cacheWrite}, {cacheWrite1H}, {knownCostUsd}, {unknownCostCount}, {cacheSavingsUsd}, {unknownCacheSavingsCount}, 1)
            ON CONFLICT ("Date", "Provider", "Model", "SourceId", "SourceKind", "UsageScope", "CostBasis") DO UPDATE SET
                "InputTokens" = "DailyAggregates"."InputTokens" + EXCLUDED."InputTokens",
                "OutputTokens" = "DailyAggregates"."OutputTokens" + EXCLUDED."OutputTokens",
                "CacheReadTokens" = "DailyAggregates"."CacheReadTokens" + EXCLUDED."CacheReadTokens",
                "CacheWriteTokens" = "DailyAggregates"."CacheWriteTokens" + EXCLUDED."CacheWriteTokens",
                "CacheWrite1hTokens" = "DailyAggregates"."CacheWrite1hTokens" + EXCLUDED."CacheWrite1hTokens",
                "CostUsd" = "DailyAggregates"."CostUsd" + EXCLUDED."CostUsd",
                "UnknownCostCount" = "DailyAggregates"."UnknownCostCount" + EXCLUDED."UnknownCostCount",
                "CacheSavingsUsd" = "DailyAggregates"."CacheSavingsUsd" + EXCLUDED."CacheSavingsUsd",
                "UnknownCacheSavingsCount" = "DailyAggregates"."UnknownCacheSavingsCount" + EXCLUDED."UnknownCacheSavingsCount",
                "RequestCount" = "DailyAggregates"."RequestCount" + EXCLUDED."RequestCount"
            """,
            ct
        );
        await tx.CommitAsync(ct);
        return new RecordEventResult(evt.Id, IsDuplicate: false);
    }

    public async Task<PurgeResult> PurgeProviderAsync(Provider provider, CancellationToken ct = default)
    {
        await using var tx = await ctx.Database.BeginTransactionAsync(ct);
        // ExecuteDeleteAsync issues a single bulk DELETE per table (no entity tracking).
        // The EventKey/Provider value converters make the enum comparison translate to SQL.
        var deletedEvents = await ctx.UsageEvents.Where(e => e.Provider == provider).ExecuteDeleteAsync(ct);
        var deletedAggregates = await ctx.DailyAggregates.Where(a => a.Provider == provider).ExecuteDeleteAsync(ct);
        await tx.CommitAsync(ct);
        return new PurgeResult(deletedEvents, deletedAggregates);
    }

    public Task UpsertDailyAggregateAsync(
        LocalDate date,
        Provider provider,
        string model,
        long inputTokens,
        long outputTokens,
        long cacheReadTokens,
        long cacheWriteTokens,
        decimal costUsd,
        int requestCount = 1,
        CancellationToken ct = default
    )
    {
        var providerStr = provider.ToString();
        const string sourceId = UsageSourceIds.LegacyApi;
        const string sourceKind = nameof(SourceKind.Legacy);
        const string usageScope = nameof(UsageScope.Unknown);
        const string costBasis = nameof(CostBasis.Unknown);
        // CacheWrite1hTokens is written as a literal 0, not a parameter: the only caller is
        // the polled-API ingest arm, and Anthropic's usage report does not break cache writes
        // down by TTL. 0 means "no one-hour portion reported", which prices the whole write at
        // the five-minute rate -- the same answer this path gave before the split existed.
        return ctx.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO "DailyAggregates" ("Date", "Provider", "Model", "SourceId", "SourceKind", "UsageScope", "CostBasis", "InputTokens", "OutputTokens", "CacheReadTokens", "CacheWriteTokens", "CacheWrite1hTokens", "CostUsd", "UnknownCostCount", "CacheSavingsUsd", "UnknownCacheSavingsCount", "RequestCount")
            VALUES ({date}, {providerStr}, {model}, {sourceId}, {sourceKind}, {usageScope}, {costBasis}, {inputTokens}, {outputTokens}, {cacheReadTokens}, {cacheWriteTokens}, 0, {costUsd}, 0, 0, {requestCount}, {requestCount})
            ON CONFLICT ("Date", "Provider", "Model", "SourceId", "SourceKind", "UsageScope", "CostBasis") DO UPDATE SET
                "InputTokens" = EXCLUDED."InputTokens",
                "OutputTokens" = EXCLUDED."OutputTokens",
                "CacheReadTokens" = EXCLUDED."CacheReadTokens",
                "CacheWriteTokens" = EXCLUDED."CacheWriteTokens",
                "CacheWrite1hTokens" = EXCLUDED."CacheWrite1hTokens",
                "CostUsd" = EXCLUDED."CostUsd",
                "UnknownCostCount" = EXCLUDED."UnknownCostCount",
                "CacheSavingsUsd" = EXCLUDED."CacheSavingsUsd",
                "UnknownCacheSavingsCount" = EXCLUDED."UnknownCacheSavingsCount",
                "RequestCount" = EXCLUDED."RequestCount"
            """,
            ct
        );
    }

    public async Task<IReadOnlyList<DailyAggregate>> GetAggregatesAsync(
        LocalDate from,
        LocalDate to,
        CancellationToken ct = default
    )
    {
        return await ctx
            .DailyAggregates.AsNoTracking()
            .Where(a => a.Date >= from && a.Date <= to)
            .OrderBy(a => a.Date)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<BudgetRule>> GetBudgetRulesAsync(CancellationToken ct = default)
    {
        return await ctx.BudgetRules.AsNoTracking().ToListAsync(ct);
    }

    public async Task SetBudgetRuleTriggeredAsync(Guid ruleId, Instant triggeredAt, CancellationToken ct = default)
    {
        await ctx
            .BudgetRules.Where(r => r.Id == ruleId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.LastTriggeredAt, triggeredAt), ct);
    }

    public async Task AddInsightAsync(Insight insight, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(insight);
        ctx.Insights.Add(insight);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<Insight>> GetUnacknowledgedInsightsAsync(CancellationToken ct = default)
    {
        return await ctx
            .Insights.AsNoTracking()
            .Where(i => i.AcknowledgedAt == null)
            .OrderByDescending(i => i.GeneratedAt)
            .ToListAsync(ct);
    }

    public async Task AcknowledgeInsightAsync(Guid insightId, Instant at, CancellationToken ct = default)
    {
        await ctx
            .Insights.Where(i => i.Id == insightId)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.AcknowledgedAt, at), ct);
    }

    public async Task<LocalDate?> GetLatestInsightPeriodEndAsync(CancellationToken ct = default)
    {
        // Exclude budget-alert insights: they carry PeriodEnd = today (a notification, not
        // an analysis of a completed day), so counting them would advance the daily-analysis
        // watermark past the current day and permanently skip that day's AI analysis.
        return await ctx
            .Insights.AsNoTracking()
            .Where(i => i.InsightType != InsightType.BudgetAlert)
            .OrderByDescending(i => i.PeriodEnd)
            .Select(i => (LocalDate?)i.PeriodEnd)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<Subscription>> GetActiveSubscriptionsAsync(
        LocalDate today,
        CancellationToken ct = default
    )
    {
        return await ctx
            .Subscriptions.AsNoTracking()
            .Where(s => s.ActiveFrom <= today && (s.ActiveTo == null || s.ActiveTo >= today))
            .ToListAsync(ct);
    }

    public async Task<PatchEventCostResult?> PatchEventCostAsync(
        Provider provider,
        string eventKey,
        decimal newCostUsd,
        CancellationToken ct = default
    )
    {
        // F3: open transaction with RepeatableRead BEFORE reading the snapshot so a concurrent
        // PATCH cannot read the same OldCostUsd, compute the same delta, and double-apply it
        // to DailyAggregates.
        await using var tx = await ctx.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);

        // F1: scope lookup to (Provider, EventKey) to match the unique index contract.
        // AsNoTracking is load-bearing here: the ExecuteUpdateAsync below writes via SQL
        // and bypasses the change tracker, so the snapshot must not be served from a
        // stale identity-map entry. Do not remove it.
        var snapshot = await ctx
            .UsageEvents.AsNoTracking()
            .Where(e => e.Provider == provider && e.EventKey == eventKey)
            .Select(e => new
            {
                e.Id,
                e.Provider,
                e.Model,
                e.OccurredAt,
                e.SourceId,
                e.SourceKind,
                e.UsageScope,
                e.CostBasis,
                e.CostUsd,
            })
            .FirstOrDefaultAsync(ct);

        if (snapshot is null)
        {
            await tx.RollbackAsync(ct);
            return null;
        }

        var oldCostUsd = snapshot.CostUsd ?? 0m;
        var wasUnknown = snapshot.CostUsd is null;
        if (!wasUnknown && oldCostUsd == newCostUsd)
        {
            await tx.RollbackAsync(ct);
            return new PatchEventCostResult(snapshot.Id, oldCostUsd, newCostUsd);
        }

        var delta = newCostUsd - oldCostUsd;
        var date = snapshot.OccurredAt.InUtc().Date;
        var model = snapshot.Model ?? "unknown";
        var providerStr = snapshot.Provider.ToString();
        var sourceKind = snapshot.SourceKind.ToString();
        var usageScope = snapshot.UsageScope.ToString();
        var costBasis = snapshot.CostBasis.ToString();

        await ctx
            .UsageEvents.Where(e => e.Id == snapshot.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.CostUsd, newCostUsd), ct);

        // Adjust the pre-aggregated daily row by the same delta; floor at 0 to guard
        // against rounding producing a tiny negative.
        // F-G1: capture row count and abort if no aggregate row was updated — prevents
        // UsageEvents and DailyAggregates drifting apart silently.
        var rowsAffected = await ctx.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE "DailyAggregates"
            SET "CostUsd" = GREATEST(0, "CostUsd" + {delta}),
                "UnknownCostCount" = "UnknownCostCount" - {(wasUnknown ? 1 : 0)}
            WHERE "Date" = {date} AND "Provider" = {providerStr} AND "Model" = {model}
              AND "SourceId" = {snapshot.SourceId} AND "SourceKind" = {sourceKind}
              AND "UsageScope" = {usageScope} AND "CostBasis" = {costBasis}
            """,
            ct
        );

        if (rowsAffected != 1)
        {
            throw new InvalidOperationException(
                $"DailyAggregates update matched {rowsAffected} rows for {providerStr}/{model}/{date}; expected 1."
            );
        }

        await tx.CommitAsync(ct);

        return new PatchEventCostResult(snapshot.Id, oldCostUsd, newCostUsd);
    }

    public async Task<IReadOnlyList<EventCostRecord>> GetEventsByProviderAsync(
        Provider provider,
        Instant? from = null,
        Instant? to = null,
        int limit = 10_000,
        CancellationToken ct = default
    )
    {
        // Defense-in-depth: the endpoint already clamps, but a 0/negative limit here would
        // make Take throw or return nothing, and an unbounded one could OOM the response.
        limit = Math.Clamp(limit, 1, 10_000);
        var query = ctx.UsageEvents.AsNoTracking().Where(e => e.Provider == provider);
        if (from is { } f)
        {
            query = query.Where(e => e.OccurredAt >= f);
        }
        if (to is { } t)
        {
            query = query.Where(e => e.OccurredAt <= t);
        }

        // ponytail: Take(limit) is a hard row ceiling so an unbounded provider can't OOM
        // the response; callers needing more page by the from/to date window.
        return await query
            .OrderBy(e => e.OccurredAt)
            .Take(limit)
            .Select(e => new EventCostRecord(
                e.Id,
                e.EventKey,
                e.Runtime,
                e.SessionId,
                e.AgentId,
                e.Model,
                e.InputTokens,
                e.OutputTokens,
                e.CacheWriteTokens,
                e.ThoughtTokens,
                e.CostUsd
            ))
            .ToListAsync(ct);
    }
}
