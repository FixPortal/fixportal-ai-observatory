using AiObservatory.Data.Entities;
using AiObservatory.Data.Repositories;
using AiObservatory.Ingest.Sources;
using NodaTime;

namespace AiObservatory.Ingest.Services.Copilot;

public class CopilotIngestionService(
    ICopilotUsageClient client,
    IUsageRepository repository,
    IClock clock,
    ILogger<CopilotIngestionService> logger
) : IUsageSource
{
    public string SourceId => UsageSourceIds.CopilotOrgReport;

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
        var record = await client.GetDailyUsageAsync(date, cancellationToken);
        if (record is null)
        {
            return null;
        }

        var observedAt = clock.GetCurrentInstant();
        var evt = new UsageEvent
        {
            Provider = Provider.Copilot,
            OccurredAt = date.AtStartOfDayInZone(DateTimeZone.Utc).ToInstant(),
            IngestedAt = observedAt,
            Model = "copilot",
            InputTokens = 0,
            OutputTokens = 0,
            CostUsd = 0m,
            EventKey = $"copilot:{date:yyyy-MM-dd}:copilot",
            RawPayload = record.RawJson,
            SourceId = SourceId,
            SourceKind = SourceKind.ProviderApi,
            UsageScope = UsageScope.Subscription,
            CostBasis = CostBasis.None,
            ObservedAt = observedAt,
        };
        await repository.RecordEventAsync(evt, cancellationToken);
        logger.LogInformation(
            "Copilot: ingested activity record for {Date} ({ActiveUsers} active users)",
            date,
            record.ActiveUsers
        );
        return record.Date.AtStartOfDayInZone(DateTimeZone.Utc).ToInstant();
    }
}
