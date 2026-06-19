using AiObservatory.Data.Entities;
using AiObservatory.Data.Repositories;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace AiObservatory.Ingest.Services.Copilot;

public class CopilotIngestionService(
    ICopilotUsageClient client,
    IUsageRepository repository,
    IClock clock,
    ILogger<CopilotIngestionService> logger)
{
    public async Task IngestAsync(LocalDate date, CancellationToken ct = default)
    {
        var record = await client.GetDailyUsageAsync(date, ct);
        if (record is null)
        {
            return;
        }

        var eventKey = $"copilot:{date:yyyy-MM-dd}:copilot";

        // Copilot billing is subscription-based — cost tracked in the subscriptions table.
        // Token counts are not available from the org billing API; use the session-end
        // extension for per-session token tracking instead.
        var evt = new UsageEvent
        {
            Provider = Provider.Copilot,
            OccurredAt = date.AtStartOfDayInZone(DateTimeZone.Utc).ToInstant(),
            IngestedAt = clock.GetCurrentInstant(),
            Model = "copilot",
            InputTokens = 0,
            OutputTokens = 0,
            CostUsd = 0m,
            EventKey = eventKey,
            RawPayload = record.RawJson
        };
        await repository.RecordEventAsync(evt, ct);
        logger.LogInformation("Copilot: ingested activity record for {Date} ({ActiveUsers} active users)", date, record.ActiveUsers);
    }
}
