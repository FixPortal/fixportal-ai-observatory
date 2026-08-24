using AiObservatory.Data.Entities;
using AiObservatory.Data.Repositories;
using AiObservatory.Ingest.Sources;
using NodaTime;

namespace AiObservatory.Ingest.Services.Google;

public class GoogleIngestionService(
    IGoogleBillingClient client,
    IUsageRepository repository,
    IClock clock,
    ILogger<GoogleIngestionService> logger
) : IUsageSource
{
    public string SourceId => UsageSourceIds.GoogleCloudBillingExport;

    public async Task<SourceIngestionResult> IngestAsync(
        LocalDate from,
        LocalDate through,
        CancellationToken cancellationToken
    )
    {
        for (var date = from; date <= through; date = date.PlusDays(1))
        {
            await IngestDayAsync(date, cancellationToken);
        }
        return new SourceIngestionResult(null);
    }

    private async Task IngestDayAsync(LocalDate date, CancellationToken cancellationToken)
    {
        var records = await client.GetDailySpendAsync(date, cancellationToken);
        var groups = records.GroupBy(r => r.Model).ToList();
        var observedAt = clock.GetCurrentInstant();

        var events =
            from g in groups
            let model = g.Key
            let cost = g.Sum(x => x.CostUsd)
            let combinedPayload = "[" + string.Join(",", g.Select(x => x.RawJson)) + "]"
            let eventKey = $"google:{date:yyyy-MM-dd}:{model}"
            select new UsageEvent
            {
                Provider = Provider.Google,
                OccurredAt = date.AtStartOfDayInZone(DateTimeZone.Utc).ToInstant(),
                IngestedAt = observedAt,
                Model = model,
                InputTokens = 0,
                OutputTokens = 0,
                CostUsd = cost,
                EventKey = eventKey,
                RawPayload = combinedPayload,
                SourceId = SourceId,
                SourceKind = SourceKind.ProviderApi,
                UsageScope = UsageScope.Api,
                CostBasis = CostBasis.Billed,
                ObservedAt = observedAt,
            };

        foreach (var evt in events)
        {
            await repository.RecordEventAsync(evt, cancellationToken);
        }

        logger.LogInformation(
            "Google: ingested {Count} billing records (grouped into {GroupCount} batches) for {Date}",
            records.Count,
            groups.Count,
            date
        );
    }
}
