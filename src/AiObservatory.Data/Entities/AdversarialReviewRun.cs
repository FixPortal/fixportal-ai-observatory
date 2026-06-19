using NodaTime;

namespace AiObservatory.Data.Entities;

public sealed class AdversarialReviewRun
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Reviewer { get; init; } = "";
    public string Model { get; init; } = "";
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public decimal CostUsd { get; init; }
    public long ReviewDurationMs { get; init; }
    public int IssuesRaised { get; init; }
    public int IssuesAccepted { get; init; }

    /// <summary>
    /// Null when IssuesAccepted is zero (guard divide-by-zero).
    /// </summary>
    public decimal? CostPerAcceptedFinding { get; init; }

    /// <summary>
    /// Client-supplied utc-timestamp-slug used for idempotency.
    /// </summary>
    public string RunId { get; init; } = "";

    public Instant RecordedAt { get; init; }
}
