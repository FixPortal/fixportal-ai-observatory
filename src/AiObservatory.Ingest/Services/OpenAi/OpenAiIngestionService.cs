using AiObservatory.Data.Entities;
using AiObservatory.Data.Pricing;
using AiObservatory.Data.Repositories;
using AiObservatory.Ingest.Sources;
using NodaTime;

namespace AiObservatory.Ingest.Services.OpenAi;

public class OpenAiIngestionService(
    IOpenAiUsageClient client,
    IUsageRepository repository,
    UsagePriceResolver priceResolver,
    IClock clock,
    ILogger<OpenAiIngestionService> logger
) : IUsageSource
{
    public string SourceId => UsageSourceIds.OpenAiUsageApi;

    public async Task<SourceIngestionResult> IngestAsync(
        LocalDate from,
        LocalDate through,
        CancellationToken cancellationToken
    )
    {
        Instant? latest = null;
        for (var date = from; date <= through; date = date.PlusDays(1))
        {
            var observed = await IngestDayAsync(date, cancellationToken);
            if (observed is { } value && (latest is null || value > latest))
            {
                latest = value;
            }
        }
        return new SourceIngestionResult(latest);
    }

    private async Task<Instant?> IngestDayAsync(LocalDate date, CancellationToken cancellationToken)
    {
        var records = await client.GetDailyUsageAsync(date, cancellationToken);
        var groups = records.GroupBy(r => r.Model).ToList();
        var observedAt = clock.GetCurrentInstant();

        var events =
            from g in groups
            let model = g.Key
            let inputTokens = g.Sum(x => x.InputTokens)
            let outputTokens = g.Sum(x => x.OutputTokens)
            let cachedTokens = g.Sum(x => x.CachedInputTokens)
            let cacheWriteTokens = g.Sum(x => x.CacheWriteTokens)
            let combinedPayload = "[" + string.Join(",", g.Select(x => x.RawJson)) + "]"
            let eventKey = $"openai:{date:yyyy-MM-dd}:{model}"
            select new UsageEvent
            {
                Provider = Provider.OpenAI,
                OccurredAt = date.AtStartOfDayInZone(DateTimeZone.Utc).ToInstant(),
                IngestedAt = observedAt,
                Model = model,
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
                CacheReadTokens = cachedTokens,
                CacheWriteTokens = cacheWriteTokens,
                CostUsd = null,
                EventKey = eventKey,
                RawPayload = combinedPayload,
                SourceId = SourceId,
                SourceKind = SourceKind.ProviderApi,
                UsageScope = UsageScope.Api,
                CostBasis = CostBasis.ListPriceEstimate,
                ObservedAt = observedAt,
            };

        foreach (var evt in events)
        {
            var quote = await priceResolver.ResolveAsync(evt, cancellationToken);
            evt.CostUsd = quote?.CostUsd;
            evt.CacheSavingsUsd = quote?.CacheSavingsUsd;
            await repository.RecordEventAsync(evt, cancellationToken);
        }

        logger.LogInformation(
            "OpenAI: ingested {Count} usage records (grouped into {GroupCount} models) for {Date}",
            records.Count,
            groups.Count,
            date
        );
        return records.Count == 0
            ? null
            : records.Max(record => record.Date).AtStartOfDayInZone(DateTimeZone.Utc).ToInstant();
    }
}
