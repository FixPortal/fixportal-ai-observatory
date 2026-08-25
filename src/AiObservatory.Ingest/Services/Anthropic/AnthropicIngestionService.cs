using AiObservatory.Data.Entities;
using AiObservatory.Data.Pricing;
using AiObservatory.Data.Repositories;
using AiObservatory.Ingest.Sources;
using NodaTime;

namespace AiObservatory.Ingest.Services.Anthropic;

public class AnthropicIngestionService(
    IAnthropicUsageClient client,
    IUsageRepository repository,
    UsagePriceResolver priceResolver,
    IClock clock,
    ILogger<AnthropicIngestionService> logger
) : IUsageSource
{
    public string SourceId => UsageSourceIds.AnthropicUsageApi;

    public async Task<SourceIngestionResult> IngestAsync(
        LocalDate from,
        LocalDate through,
        CancellationToken cancellationToken
    )
    {
        Instant? latest = null;
        for (var date = from; date <= through; date = date.PlusDays(1))
        {
            var dayLatest = await IngestDayAsync(date, cancellationToken);
            if (dayLatest is { } observed && (latest is null || observed > latest))
            {
                latest = observed;
            }
        }
        return new SourceIngestionResult(latest);
    }

    private async Task<Instant?> IngestDayAsync(LocalDate date, CancellationToken cancellationToken)
    {
        var records = await client.GetUsageAsync(date, cancellationToken);
        var groups = records.GroupBy(r => new { r.Date, r.Model }).ToList();
        var observedAt = clock.GetCurrentInstant();

        var events =
            from g in groups
            let rDate = g.Key.Date
            let model = g.Key.Model
            let input = g.Sum(x => x.InputTokens)
            let output = g.Sum(x => x.OutputTokens)
            let cacheRead = g.Sum(x => x.CacheReadTokens)
            let cacheWrite = g.Sum(x => x.CacheWriteTokens)
            let combinedPayload = "[" + string.Join(",", g.Select(x => x.RawJson)) + "]"
            let eventKey = $"anthropic:{rDate:yyyy-MM-dd}:{model}"
            select new UsageEvent
            {
                Provider = Provider.Anthropic,
                OccurredAt = rDate.AtStartOfDayInZone(DateTimeZone.Utc).ToInstant(),
                IngestedAt = observedAt,
                Model = model,
                InputTokens = input,
                OutputTokens = output,
                CacheReadTokens = cacheRead,
                CacheWriteTokens = cacheWrite,
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
            "Anthropic: ingested {Count} records (grouped into {GroupCount} batches) for {Date}",
            records.Count,
            groups.Count,
            date
        );
        return records.Count == 0
            ? null
            : records.Max(record => record.Date).AtStartOfDayInZone(DateTimeZone.Utc).ToInstant();
    }
}
