namespace AiObservatory.Api.Services;

/// <summary>
/// Fans a budget alert out to both delivery channels. Email keeps its existing at-least-once
/// retry semantics from before this class existed: its failure propagates unchanged, so
/// <c>BudgetAlertService</c>'s lease is released and the whole delivery retries. Slack is a
/// best-effort secondary channel fenced by <c>BudgetAlertClaim.SlackSentAt</c> (see
/// <see cref="SlackAlertNotifier"/>): a failure is logged and never retried in isolation, and
/// never blocks email from being attempted or from correctly reporting its own outcome upward.
/// Because Slack runs first inside this same <see cref="NotifyAsync"/> call, it is attempted on
/// every email lease-retry pass, but the fence makes every attempt after the first a no-op --
/// Slack fires at most once per claim, independent of how many times email itself is retried.
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
