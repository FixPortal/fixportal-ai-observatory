namespace AiObservatory.Api.Services;

/// <summary>
/// Durable budget-alert deliveries have at-least-once attempt semantics. Retries preserve
/// <see cref="BudgetAlertPayload.MessageId"/>, but SMTP can accept a message before the sender
/// observes failure, so recipients may still see a duplicate. No exactly-once claim is made.
/// </summary>
public record BudgetAlertPayload(
    string Provider,
    string Period,
    decimal ThresholdGbp,
    decimal ActualSpendGbp,
    DateTimeOffset TriggeredAt,
    string MessageId,
    Guid ClaimId
);

public interface IAlertNotifier
{
    Task NotifyAsync(BudgetAlertPayload payload, CancellationToken ct = default);
}
