using NodaTime;

namespace AiObservatory.Ingest.Services.Anthropic;

public interface IAnthropicUsageClient
{
    Task<IReadOnlyList<AnthropicUsageRecord>> GetUsageAsync(LocalDate date, CancellationToken ct = default);
}
