using AiObservatory.Data.Entities;
using AiObservatory.Data.Repositories;
using NodaTime;

namespace AiObservatory.Ingest.Services.Google;

public class GoogleIngestionService(
    IGoogleBillingClient client,
    IUsageRepository repository,
    IClock clock,
    ILogger<GoogleIngestionService> logger)
{
    public async Task IngestAsync(LocalDate date, CancellationToken ct = default)
    {
        var records = await client.GetDailySpendAsync(date, ct);
        var groups = records.GroupBy(r => r.Model).ToList();

        foreach (var evt in from g in groups
                 let model = g.Key
                 let cost = g.Sum(x => x.CostUsd)
                 let combinedPayload = "[" + string.Join(",", g.Select(x => x.RawJson)) + "]"
                 let eventKey = $"google:{date:yyyy-MM-dd}:{model}"
                 select new UsageEvent
                 {
                     Provider = Provider.Google,
                     OccurredAt = date.AtStartOfDayInZone(DateTimeZone.Utc).ToInstant(),
                     IngestedAt = clock.GetCurrentInstant(),
                     Model = model,
                     InputTokens = 0,
                     OutputTokens = 0,
                     CostUsd = cost,
                     EventKey = eventKey,
                     RawPayload = combinedPayload
                 })
        {
            await repository.RecordEventAsync(evt, ct);
        }

        logger.LogInformation("Google: ingested {Count} billing records (grouped into {GroupCount} batches) for {Date}", records.Count, groups.Count(), date);
    }
}
