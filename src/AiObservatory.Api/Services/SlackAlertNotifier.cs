using System.Net.Http.Json;
using AiObservatory.Data.Repositories;
using NodaTime;

namespace AiObservatory.Api.Services;

/// <summary>
/// Posts a Slack incoming-webhook message. Best-effort: no retry -- a failure is logged and
/// swallowed by the caller (<see cref="CompositeAlertNotifier"/>), never surfaced as a delivery
/// failure that would cause <c>BudgetAlertService</c> to re-attempt the whole payload (which
/// would re-send email too). Fenced by <see cref="BudgetAlertClaim.SlackSentAt"/> so a claim
/// still being retried by the email lease (see <c>BudgetAlertService.DeliverEmailAsync</c>)
/// only ever gets one Slack attempt, not one per email retry cycle.
/// </summary>
public sealed class SlackAlertNotifier(
    HttpClient http,
    IUsageRepository repository,
    IClock clock,
    ILogger<SlackAlertNotifier> logger
) : IAlertNotifier
{
    public async Task<AlertDeliveryResult> NotifyAsync(BudgetAlertPayload payload, CancellationToken ct = default)
    {
        var settings = await repository.GetNotificationSettingsAsync(ct);
        var webhookUrl = settings?.SlackWebhookUrl;
        if (string.IsNullOrEmpty(webhookUrl))
        {
            return AlertDeliveryResult.NoRecipientConfigured;
        }

        if (await repository.GetBudgetAlertSlackSentAsync(payload.ClaimId, ct))
        {
            // Fenced by a previous pass: the alert already reached Slack, so this channel
            // genuinely delivered even though this call itself posts nothing.
            return AlertDeliveryResult.Sent;
        }

        var text =
            $"*Budget alert: {payload.Provider} {payload.Period} billed spend exceeded £{payload.ThresholdGbp:F2}*\n"
            + $"Total {payload.Period.ToLowerInvariant()} billed spend for {payload.Provider} reached £{payload.ActualSpendGbp:F2}, "
            + $"exceeding your £{payload.ThresholdGbp:F2} threshold.";

        using var response = await http.PostAsJsonAsync(webhookUrl, new { text }, ct);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogError(
                "Slack webhook delivery failed with status {StatusCode} for budget alert {MessageId}",
                response.StatusCode,
                payload.MessageId
            );
            return AlertDeliveryResult.Failed;
        }

        await repository.MarkBudgetAlertSlackSentAsync(payload.ClaimId, clock.GetCurrentInstant(), ct);
        return AlertDeliveryResult.Sent;
    }
}
