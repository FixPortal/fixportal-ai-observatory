using AiObservatory.Data.Entities;
using AiObservatory.Data.Repositories;
using Microsoft.Extensions.Logging;
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
        var groups = records.GroupBy(r => new { r.Date, r.Model });
        
        foreach (var g in groups)
        {
            var rDate = g.Key.Date;
            var model = g.Key.Model;
            
            var input = g.Sum(x => x.InputTokens);
            var output = g.Sum(x => x.OutputTokens);
            var cacheRead = g.Sum(x => x.CacheReadTokens);
            var cacheWrite = g.Sum(x => x.CacheWriteTokens);
            var cost = g.Sum(x => x.CostUsd);
            
            var combinedPayload = "[" + string.Join(",", g.Select(x => x.RawJson)) + "]";
            var eventKey = $"anthropic:{rDate:yyyy-MM-dd}:{model}";

            var evt = new UsageEvent
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
            };
            
            await repository.RecordEventAsync(evt, ct);
        }
        logger.LogInformation("Anthropic: ingested {Count} records (grouped into {GroupCount} batches) for {Date}", records.Count, groups.Count(), date);
    }
}
