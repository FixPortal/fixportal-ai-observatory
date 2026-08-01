using NodaTime;

namespace AiObservatory.Ingest.Services.OpenAi;

public interface IOpenAiUsageClient
{
    Task<IReadOnlyList<OpenAiUsageRecord>> GetDailyUsageAsync(LocalDate date, CancellationToken ct = default);
}
