using NodaTime;

namespace AiObservatory.Ingest.Services.Google;

public interface IGoogleBillingClient
{
    Task<IReadOnlyList<GoogleBillingRecord>> GetDailySpendAsync(LocalDate date, CancellationToken ct = default);
}
