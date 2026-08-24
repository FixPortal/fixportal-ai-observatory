using NodaTime;

namespace AiObservatory.Ingest.Sources;

public interface IUsageSource
{
    string SourceId { get; }

    Task<SourceIngestionResult> IngestAsync(LocalDate from, LocalDate through, CancellationToken cancellationToken);
}

public sealed record SourceIngestionResult(Instant? LatestObservationAt);

public sealed record SourceDefinition(string SourceId, bool IsConfigured, Duration ExpectedRefreshInterval);

public sealed class SourceUnavailableException(string message) : Exception(message);
