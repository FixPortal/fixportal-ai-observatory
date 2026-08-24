using NodaTime;

namespace AiObservatory.Data.Entities;

public sealed class UsageEvent
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Provider Provider { get; set; }
    public Instant OccurredAt { get; set; }
    public Instant IngestedAt { get; init; }
    public string? Model { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long? CacheReadTokens { get; set; }
    public long? CacheWriteTokens { get; set; }

    /// <summary>
    /// The one-hour-TTL subset of <see cref="CacheWriteTokens"/>; the remainder is
    /// five-minute. Anthropic bills the two at different multiples of base input (2x vs
    /// 1.25x), so the split is what makes the cache-write line cost correctly. Null means
    /// the producer reported no breakdown, which prices as all-five-minute.
    /// </summary>
    // ReSharper disable once InconsistentNaming
    public long? CacheWrite1hTokens { get; set; }

    public long? ThoughtTokens { get; set; }
    public decimal? CostUsd { get; set; }
    public decimal? CacheSavingsUsd { get; set; }
    public string? Runtime { get; set; }
    public string? SessionId { get; set; }
    public string? AgentId { get; set; }
    public string RawPayload { get; set; } = "{}";
    public string SourceId { get; set; } = UsageSourceIds.LegacyApi;
    public SourceKind SourceKind { get; set; } = SourceKind.Legacy;
    public UsageScope UsageScope { get; set; } = UsageScope.Unknown;
    public CostBasis CostBasis { get; set; } = CostBasis.Unknown;
    public Instant ObservedAt { get; set; }

    /// <summary>
    /// Optional source-scoped snapshot key. Repeated identical submissions are no-ops;
    /// changed submissions replace the stored event and repair its aggregate atomically.
    /// </summary>
    public string? EventKey { get; init; }
}
