using NodaTime;

namespace AiObservatory.Data.Entities;

/// <summary>
/// Singleton row (no per-user/per-tenant scoping exists anywhere else in this app) holding
/// where budget-threshold alerts are delivered. SMTP server credentials are NOT here -- they
/// stay env-var (infra config, not a per-preference setting).
/// </summary>
public sealed class NotificationSettings
{
    // Fixed well-known id: this is a singleton row (see class doc), so a stable PK
    // avoids the entity minting a fresh random one every time it is constructed.
    public Guid Id { get; init; } = Guid.Parse("33333333-3333-3333-3333-333333333301");
    public string? AlertEmailTo { get; set; }
    public string? SlackWebhookUrl { get; set; }
    public Instant UpdatedAt { get; set; }
}
