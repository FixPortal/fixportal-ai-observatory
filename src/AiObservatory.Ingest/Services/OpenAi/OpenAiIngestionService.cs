using AiObservatory.Data.Entities;
using AiObservatory.Data.Repositories;
using Microsoft.Extensions.Logging;
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
        var groups = records.GroupBy(r => r.Model);

        foreach (var g in groups)
        {
            var model = g.Key;
            var inputTokens = g.Sum(x => x.InputTokens);
            var outputTokens = g.Sum(x => x.OutputTokens);
            var cachedTokens = g.Sum(x => x.CachedInputTokens);
            var cost = g.Sum(x => x.CostUsd);
            var combinedPayload = "[" + string.Join(",", g.Select(x => x.RawJson)) + "]";
            var eventKey = $"openai:{date:yyyy-MM-dd}:{model}";

            var evt = new UsageEvent
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
            };

            await repository.RecordEventAsync(evt, ct);
        }

        logger.LogInformation("OpenAI: ingested {Count} usage records (grouped into {GroupCount} models) for {Date}",
            records.Count, groups.Count(), date);
    }
}
