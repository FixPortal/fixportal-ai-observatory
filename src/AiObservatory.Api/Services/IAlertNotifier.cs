namespace AiObservatory.Api.Services;

public record BudgetAlertPayload(
    string Provider,
    string Period,
    decimal ThresholdUsd,
    decimal ActualSpend,
    DateTimeOffset TriggeredAt);

public interface IAlertNotifier
{
    Task NotifyAsync(BudgetAlertPayload payload, CancellationToken ct = default);
}
