using NodaTime;

namespace AiObservatory.Ingest.Services.Google;

public interface IGoogleBillingExportClient
{
    Task<IReadOnlyList<GoogleBillingRecord>> GetBillingRecordsAsync(
        Instant from,
        Instant throughExclusive,
        Instant changesSince,
        CancellationToken cancellationToken = default
    );
}
