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
    public decimal CostUsd { get; init; }
    public string RawPayload { get; init; } = "{}";

    /// <summary>
    /// Optional client-supplied idempotency key. When present, repeat submissions
    /// with the same key are ignored rather than recorded (and aggregated) twice.
    /// </summary>
    public string? EventKey { get; init; }
}
