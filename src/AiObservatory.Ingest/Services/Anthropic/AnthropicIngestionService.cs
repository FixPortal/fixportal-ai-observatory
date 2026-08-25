using System.Text.Json;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Repositories;
using AiObservatory.Ingest.Sources;
using NodaTime;

namespace AiObservatory.Ingest.Services.Anthropic;

public class AnthropicIngestionService(
    IAnthropicUsageClient client,
    IUsageRepository repository,
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
        var groups = records
            .GroupBy(r => new
            {
                r.Date,
                r.Model,
                r.ServiceTier,
                r.InferenceGeo,
                r.Speed,
            })
            .ToList();
        var observedAt = clock.GetCurrentInstant();

        var events =
            from g in groups
            let rDate = g.Key.Date
            let model = g.Key.Model
            let serviceTier = g.Key.ServiceTier
            let inferenceGeo = g.Key.InferenceGeo
            let speed = g.Key.Speed
            let input = g.Sum(x => x.InputTokens)
            let output = g.Sum(x => x.OutputTokens)
            let cacheRead = g.Sum(x => x.CacheReadTokens)
            let cacheWrite5m = g.Sum(x => x.CacheWrite5mTokens)
            let cacheWrite1h = g.Sum(x => x.CacheWrite1hTokens)
            let cacheWrite = checked(cacheWrite5m + cacheWrite1h)
            let combinedPayload = BuildPricingEvidence(g, cacheWrite5m, cacheWrite1h)
            let eventKey = $"anthropic:{rDate:yyyy-MM-dd}:{model}:{serviceTier ?? "unknown"}:{inferenceGeo ?? "unknown"}:{speed ?? "unknown"}"
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
                CacheWrite1hTokens = cacheWrite1h,
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
            await repository.RecordEstimatedEventAsync(evt, cancellationToken);
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

    private static string BuildPricingEvidence(
        IEnumerable<AnthropicUsageRecord> records,
        long cacheWrite5m,
        long cacheWrite1h
    )
    {
        var rows = records.ToArray();
        return JsonSerializer.Serialize(
            new
            {
                service_tier = rows[0].ServiceTier,
                inference_geo = rows[0].InferenceGeo,
                speed = rows[0].Speed,
                cache_creation = new
                {
                    ephemeral_5m_input_tokens = cacheWrite5m,
                    ephemeral_1h_input_tokens = cacheWrite1h,
                },
                provider_records = rows.Select(row => JsonSerializer.Deserialize<JsonElement>(row.RawJson)),
            }
        );
    }
}
