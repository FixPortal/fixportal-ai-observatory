using NodaTime;

namespace AiObservatory.Data.Entities;

public sealed class UsageEvent
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Provider Provider { get; init; }
    public Instant OccurredAt { get; init; }
    public Instant IngestedAt { get; init; }
    public string? Model { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public long? CacheReadTokens { get; init; }
    public long? CacheWriteTokens { get; init; }

    /// <summary>
    /// The one-hour-TTL subset of <see cref="CacheWriteTokens"/>; the remainder is
    /// five-minute. Anthropic bills the two at different multiples of base input (2x vs
    /// 1.25x), so the split is what makes the cache-write line cost correctly. Null means
    /// the producer reported no breakdown, which prices as all-five-minute.
    /// </summary>
    // ReSharper disable once InconsistentNaming
    public long? CacheWrite1hTokens { get; init; }

    public decimal CostUsd { get; init; }
    public string RawPayload { get; init; } = "{}";

    /// <summary>
    /// Optional client-supplied idempotency key. When present, repeat submissions
    /// with the same key are ignored rather than recorded (and aggregated) twice.
    /// </summary>
    public string? EventKey { get; init; }
}
