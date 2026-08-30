using NodaTime;

namespace AiObservatory.Data.Entities;

/// <summary>
/// Singleton row (no per-user/per-tenant scoping exists anywhere else in this app) holding
/// where budget-threshold alerts are delivered. SMTP server credentials are NOT here -- they
/// stay env-var (infra config, not a per-preference setting).
/// </summary>
public sealed class NotificationSettings
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string? AlertEmailTo { get; set; }
    public string? SlackWebhookUrl { get; set; }
    public Instant UpdatedAt { get; set; }
}
