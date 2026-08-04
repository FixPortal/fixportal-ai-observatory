using NodaTime;

namespace AiObservatory.Data.Entities;

public sealed class IdeEvent
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid PartnerId { get; init; }
    public string IdempotencyKey { get; init; } = "";
    public string EventType { get; init; } = "";
    public string EnvelopeJson { get; init; } = "";
    public string ContentSha256 { get; init; } = "";
    public Instant OccurredAt { get; init; }
    public Instant ReceivedAt { get; init; }
}
