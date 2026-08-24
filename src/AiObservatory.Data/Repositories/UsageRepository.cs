using System.Text.Json;
using AiObservatory.Data.Entities;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Npgsql;

namespace AiObservatory.Data.Repositories;

public class UsageRepository(AiObservatoryDbContext ctx) : IUsageRepository
{
    private static readonly JsonDocumentOptions RawPayloadJsonOptions = new() { AllowDuplicateProperties = false };

    public async Task<RecordEventResult> RecordEventAsync(UsageEvent evt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(evt);
        JsonDocument.Parse(evt.RawPayload, RawPayloadJsonOptions).Dispose();
        if (evt.ObservedAt == default)
        {
            evt.ObservedAt = evt.IngestedAt;
        }
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await using var tx = await ctx.Database.BeginTransactionAsync(ct);
            try
            {
                var existing = evt.EventKey is null
                    ? null
                    : await FindEventForUpdateAsync(evt.SourceId, evt.EventKey, ct);
                var result = await ApplyLockedSnapshotAsync(existing, evt, ct);
                if (result.Disposition == RecordEventDisposition.Unchanged)
                {
                    await tx.RollbackAsync(ct);
                    return result;
                }

                await ctx.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return result;
            }
            catch (DbUpdateException ex)
                when (attempt == 0
                    && evt.EventKey is not null
                    && ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation }
                )
            {
                await tx.RollbackAsync(ct);
                ctx.ChangeTracker.Clear();
            }
            catch
            {
                await tx.RollbackAsync(ct);
                ctx.ChangeTracker.Clear();
                throw;
            }
        }

        throw new InvalidOperationException("Concurrent usage-event insert retry did not resolve the source key.");
    }

    private async Task<RecordEventResult> ApplyLockedSnapshotAsync(
        UsageEvent? existing,
        UsageEvent evt,
        CancellationToken ct
    )
    {
        if (existing is null)
        {
            ctx.UsageEvents.Add(evt);
            await ApplyAggregateDeltaAsync(evt, +1, ct);
            return new RecordEventResult(evt.Id, RecordEventDisposition.Created);
        }

        if (CanonicalEquals(existing, evt))
        {
            return new RecordEventResult(existing.Id, RecordEventDisposition.Unchanged);
        }

        await ApplyAggregateDeltaAsync(existing, -1, ct);
        CopyCanonicalValues(existing, evt);
        await ApplyAggregateDeltaAsync(existing, +1, ct);
        return new RecordEventResult(existing.Id, RecordEventDisposition.Corrected);
    }

    private async Task ApplyAggregateDeltaAsync(UsageEvent evt, int sign, CancellationToken ct)
    {
        var date = evt.OccurredAt.InUtc().Date;
        var provider = evt.Provider.ToString();
        var model = evt.Model ?? "unknown";
        var sourceKind = evt.SourceKind.ToString();
        var usageScope = evt.UsageScope.ToString();
        var costBasis = evt.CostBasis.ToString();
        var inputDelta = checked(evt.InputTokens * sign);
        var outputDelta = checked(evt.OutputTokens * sign);
        var cacheReadDelta = checked((evt.CacheReadTokens ?? 0L) * sign);
        var cacheWriteDelta = checked((evt.CacheWriteTokens ?? 0L) * sign);
        var cacheWrite1hDelta = checked((evt.CacheWrite1hTokens ?? 0L) * sign);
        var costDelta = (evt.CostUsd ?? 0m) * sign;
        var unknownCostDelta = (evt.CostUsd is null ? 1 : 0) * sign;
        var cacheSavingsDelta = (evt.CacheSavingsUsd ?? 0m) * sign;
        var unknownCacheSavingsDelta = (evt.CacheSavingsUsd is null ? 1 : 0) * sign;
        var requestDelta = sign;
        var insertInput = Math.Max(0, inputDelta);
        var insertOutput = Math.Max(0, outputDelta);
        var insertCacheRead = Math.Max(0, cacheReadDelta);
        var insertCacheWrite = Math.Max(0, cacheWriteDelta);
        var insertCacheWrite1h = Math.Max(0, cacheWrite1hDelta);
        var insertCost = Math.Max(0, costDelta);
        var insertUnknownCost = Math.Max(0, unknownCostDelta);
        var insertCacheSavings = Math.Max(0, cacheSavingsDelta);
        var insertUnknownCacheSavings = Math.Max(0, unknownCacheSavingsDelta);
        var insertRequest = Math.Max(0, requestDelta);

        await ctx.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO "DailyAggregates" ("Date", "Provider", "Model", "SourceId", "SourceKind", "UsageScope", "CostBasis", "InputTokens", "OutputTokens", "CacheReadTokens", "CacheWriteTokens", "CacheWrite1hTokens", "CostUsd", "UnknownCostCount", "CacheSavingsUsd", "UnknownCacheSavingsCount", "RequestCount")
            VALUES ({date}, {provider}, {model}, {evt.SourceId}, {sourceKind}, {usageScope}, {costBasis}, {insertInput}, {insertOutput}, {insertCacheRead}, {insertCacheWrite}, {insertCacheWrite1h}, {insertCost}, {insertUnknownCost}, {insertCacheSavings}, {insertUnknownCacheSavings}, {insertRequest})
            ON CONFLICT ("Date", "Provider", "Model", "SourceId", "SourceKind", "UsageScope", "CostBasis") DO UPDATE SET
                "InputTokens" = "DailyAggregates"."InputTokens" + {inputDelta},
                "OutputTokens" = "DailyAggregates"."OutputTokens" + {outputDelta},
                "CacheReadTokens" = "DailyAggregates"."CacheReadTokens" + {cacheReadDelta},
                "CacheWriteTokens" = "DailyAggregates"."CacheWriteTokens" + {cacheWriteDelta},
                "CacheWrite1hTokens" = "DailyAggregates"."CacheWrite1hTokens" + {cacheWrite1hDelta},
                "CostUsd" = "DailyAggregates"."CostUsd" + {costDelta},
                "UnknownCostCount" = "DailyAggregates"."UnknownCostCount" + {unknownCostDelta},
                "CacheSavingsUsd" = "DailyAggregates"."CacheSavingsUsd" + {cacheSavingsDelta},
                "UnknownCacheSavingsCount" = "DailyAggregates"."UnknownCacheSavingsCount" + {unknownCacheSavingsDelta},
                "RequestCount" = "DailyAggregates"."RequestCount" + {requestDelta}
            """,
            ct
        );

        await ctx
            .DailyAggregates.Where(a =>
                a.Date == date
                && a.Provider == evt.Provider
                && a.Model == model
                && a.SourceId == evt.SourceId
                && a.SourceKind == evt.SourceKind
                && a.UsageScope == evt.UsageScope
                && a.CostBasis == evt.CostBasis
                && a.RequestCount == 0
            )
            .ExecuteDeleteAsync(ct);
    }

    private async Task<UsageEvent?> FindEventForUpdateAsync(string sourceId, string eventKey, CancellationToken ct)
    {
        var existing = await ctx
            .UsageEvents.FromSqlInterpolated(
                $"""SELECT * FROM "UsageEvents" WHERE "SourceId" = {sourceId} AND "EventKey" = {eventKey} FOR UPDATE"""
            )
            .SingleOrDefaultAsync(ct);
        if (existing is not null)
        {
            await ctx.Entry(existing).ReloadAsync(ct);
        }

        return existing;
    }

    private static bool CanonicalEquals(UsageEvent left, UsageEvent right) =>
        left.Provider == right.Provider
        && left.OccurredAt == right.OccurredAt
        && left.Model == right.Model
        && left.InputTokens == right.InputTokens
        && left.OutputTokens == right.OutputTokens
        && left.CacheReadTokens == right.CacheReadTokens
        && left.CacheWriteTokens == right.CacheWriteTokens
        && left.CacheWrite1hTokens == right.CacheWrite1hTokens
        && left.ThoughtTokens == right.ThoughtTokens
        && left.CostUsd == right.CostUsd
        && left.CacheSavingsUsd == right.CacheSavingsUsd
        && left.Runtime == right.Runtime
        && left.SessionId == right.SessionId
        && left.AgentId == right.AgentId
        && JsonEquals(left.RawPayload, right.RawPayload)
        && left.SourceId == right.SourceId
        && left.SourceKind == right.SourceKind
        && left.UsageScope == right.UsageScope
        && left.CostBasis == right.CostBasis
        && left.EventKey == right.EventKey;

    private static bool JsonEquals(string left, string right)
    {
        using var leftJson = JsonDocument.Parse(left, RawPayloadJsonOptions);
        using var rightJson = JsonDocument.Parse(right, RawPayloadJsonOptions);
        return JsonElement.DeepEquals(leftJson.RootElement, rightJson.RootElement);
    }

    private static void CopyCanonicalValues(UsageEvent target, UsageEvent source)
    {
        target.Provider = source.Provider;
        target.OccurredAt = source.OccurredAt;
        target.Model = source.Model;
        target.InputTokens = source.InputTokens;
        target.OutputTokens = source.OutputTokens;
        target.CacheReadTokens = source.CacheReadTokens;
        target.CacheWriteTokens = source.CacheWriteTokens;
        target.CacheWrite1hTokens = source.CacheWrite1hTokens;
        target.ThoughtTokens = source.ThoughtTokens;
        target.CostUsd = source.CostUsd;
        target.CacheSavingsUsd = source.CacheSavingsUsd;
        target.Runtime = source.Runtime;
        target.SessionId = source.SessionId;
        target.AgentId = source.AgentId;
        target.RawPayload = source.RawPayload;
        target.SourceId = source.SourceId;
        target.SourceKind = source.SourceKind;
        target.UsageScope = source.UsageScope;
        target.CostBasis = source.CostBasis;
        target.ObservedAt = source.ObservedAt;
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
        string sourceId,
        string eventKey,
        decimal newCostUsd,
        CancellationToken ct = default
    )
    {
        await using var tx = await ctx.Database.BeginTransactionAsync(ct);
        try
        {
            var existing = await FindEventForUpdateAsync(sourceId, eventKey, ct);
            if (existing is null || existing.Provider != provider)
            {
                await tx.RollbackAsync(ct);
                return null;
            }

            var oldCostUsd = existing.CostUsd ?? 0m;
            var replacement = CopyWithCost(existing, newCostUsd);
            var result = await ApplyLockedSnapshotAsync(existing, replacement, ct);
            if (result.Disposition == RecordEventDisposition.Unchanged)
            {
                await tx.RollbackAsync(ct);
                return new PatchEventCostResult(existing.Id, oldCostUsd, newCostUsd);
            }

            await ctx.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new PatchEventCostResult(existing.Id, oldCostUsd, newCostUsd);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            ctx.ChangeTracker.Clear();
            throw;
        }
    }

    private static UsageEvent CopyWithCost(UsageEvent source, decimal costUsd) =>
        new()
        {
            Provider = source.Provider,
            OccurredAt = source.OccurredAt,
            IngestedAt = source.IngestedAt,
            Model = source.Model,
            InputTokens = source.InputTokens,
            OutputTokens = source.OutputTokens,
            CacheReadTokens = source.CacheReadTokens,
            CacheWriteTokens = source.CacheWriteTokens,
            CacheWrite1hTokens = source.CacheWrite1hTokens,
            ThoughtTokens = source.ThoughtTokens,
            CostUsd = costUsd,
            CacheSavingsUsd = source.CacheSavingsUsd,
            Runtime = source.Runtime,
            SessionId = source.SessionId,
            AgentId = source.AgentId,
            RawPayload = source.RawPayload,
            SourceId = source.SourceId,
            SourceKind = source.SourceKind,
            UsageScope = source.UsageScope,
            CostBasis = source.CostBasis,
            ObservedAt = source.ObservedAt,
            EventKey = source.EventKey,
        };

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

    public async Task<IReadOnlyList<LocalSnapshotRecord>> GetLocalSnapshotsAsync(
        string sourceId,
        CancellationToken ct = default
    ) =>
        await ctx
            .UsageEvents.AsNoTracking()
            .Where(e =>
                e.SourceId == sourceId
                && e.SourceKind == SourceKind.LocalTelemetry
                && e.EventKey != null
                && (
                    e.InputTokens > 0
                    || e.OutputTokens > 0
                    || (e.CacheReadTokens ?? 0) > 0
                    || (e.CacheWriteTokens ?? 0) > 0
                )
            )
            .OrderBy(e => e.EventKey)
            .Select(e => new LocalSnapshotRecord(
                e.Provider,
                e.OccurredAt,
                e.Model,
                e.CostUsd == null ? null : 0m,
                e.Runtime,
                e.SourceId,
                e.SourceKind,
                e.UsageScope,
                e.CostBasis,
                e.EventKey!
            ))
            .ToListAsync(ct);
}
