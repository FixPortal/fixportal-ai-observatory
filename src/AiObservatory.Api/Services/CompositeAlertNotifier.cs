namespace AiObservatory.Api.Services;

/// <summary>
/// Fans a budget alert out to both delivery channels. Email keeps its existing at-least-once
/// retry semantics from before this class existed: its failure propagates unchanged, so
/// <c>BudgetAlertService</c>'s lease is released and the whole delivery retries. Slack is a
/// best-effort secondary channel with no lease of its own -- its failure is logged, never
/// retried, and never blocks email from being attempted or from correctly reporting its own
/// outcome upward.
/// </summary>
public sealed class CompositeAlertNotifier(
    [FromKeyedServices("email")] IAlertNotifier email,
    [FromKeyedServices("slack")] IAlertNotifier slack,
    ILogger<CompositeAlertNotifier> logger
) : IAlertNotifier
{
    public async Task NotifyAsync(BudgetAlertPayload payload, CancellationToken ct = default)
    {
        try
        {
            await slack.NotifyAsync(payload, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Slack alert delivery failed for budget alert {MessageId}", payload.MessageId);
        }

        await email.NotifyAsync(payload, ct);
    }
}
