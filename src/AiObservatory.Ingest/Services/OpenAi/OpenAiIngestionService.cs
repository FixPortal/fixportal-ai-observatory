using AiObservatory.Data.Entities;
using AiObservatory.Data.Repositories;
using NodaTime;

namespace AiObservatory.Ingest.Services.OpenAi;

public class OpenAiIngestionService(
    IOpenAiUsageClient client,
    IUsageRepository repository,
    IClock clock,
    ILogger<OpenAiIngestionService> logger)
{
    public async Task IngestAsync(LocalDate date, CancellationToken ct = default)
    {
        var records = await client.GetDailyUsageAsync(date, ct);
        var groups = records.GroupBy(r => r.Model).ToList();

        foreach (var evt in from g in groups
                 let model = g.Key
                 let inputTokens = g.Sum(x => x.InputTokens)
                 let outputTokens = g.Sum(x => x.OutputTokens)
                 let cachedTokens = g.Sum(x => x.CachedInputTokens)
                 let cost = g.Sum(x => x.CostUsd)
                 let combinedPayload = "[" + string.Join(",", g.Select(x => x.RawJson)) + "]"
                 let eventKey = $"openai:{date:yyyy-MM-dd}:{model}"
                 select new UsageEvent
                 {
                     Provider = Provider.OpenAI,
                     OccurredAt = date.AtStartOfDayInZone(DateTimeZone.Utc).ToInstant(),
                     IngestedAt = clock.GetCurrentInstant(),
                     Model = model,
                     InputTokens = inputTokens,
                     OutputTokens = outputTokens,
                     CacheReadTokens = cachedTokens,
                     CostUsd = cost,
                     EventKey = eventKey,
                     RawPayload = combinedPayload
                 })
        {
            await repository.RecordEventAsync(evt, ct);
        }


        logger.LogInformation("OpenAI: ingested {Count} usage records (grouped into {GroupCount} models) for {Date}",
            records.Count, groups.Count(), date);
    }
}
