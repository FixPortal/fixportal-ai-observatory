using NodaTime;

namespace AiObservatory.Ingest.Services.Copilot;

public interface ICopilotUsageClient
{
    Task<CopilotUsageRecord?> GetDailyUsageAsync(LocalDate date, CancellationToken ct = default);
}
