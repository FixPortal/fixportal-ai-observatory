using AiObservatory.Data.Entities;
using AiObservatory.Data.Repositories;
using NodaTime;

namespace AiObservatory.Ingest.Services.Anthropic;

public class AnthropicIngestionService(
    IAnthropicUsageClient client,
    IUsageRepository repository,
    IClock clock,
    ILogger<AnthropicIngestionService> logger)
{
    public async Task IngestAsync(LocalDate date, CancellationToken ct = default)
    {
        var records = await client.GetUsageAsync(date, ct);
        var groups = records.GroupBy(r => new { r.Date, r.Model }).ToList();

        foreach (var evt in from g in groups
                            let rDate = g.Key.Date
                            let model = g.Key.Model
                            let input = g.Sum(x => x.InputTokens)
                            let output = g.Sum(x => x.OutputTokens)
                            let cacheRead = g.Sum(x => x.CacheReadTokens)
                            let cacheWrite = g.Sum(x => x.CacheWriteTokens)
                            let cost = g.Sum(x => x.CostUsd)
                            let combinedPayload = "[" + string.Join(",", g.Select(x => x.RawJson)) + "]"
                            let eventKey = $"anthropic:{rDate:yyyy-MM-dd}:{model}"
                            select new UsageEvent
                            {
                                Provider = Provider.Anthropic,
                                OccurredAt = rDate.AtStartOfDayInZone(DateTimeZone.Utc).ToInstant(),
                                IngestedAt = clock.GetCurrentInstant(),
                                Model = model,
                                InputTokens = input,
                                OutputTokens = output,
                                CacheReadTokens = cacheRead,
                                CacheWriteTokens = cacheWrite,
                                CostUsd = cost,
                                EventKey = eventKey,
                                RawPayload = combinedPayload
                            })
        {
            await repository.RecordEventAsync(evt, ct);
        }

        logger.LogInformation("Anthropic: ingested {Count} records (grouped into {GroupCount} batches) for {Date}", records.Count, groups.Count,
            date);
    }
}
