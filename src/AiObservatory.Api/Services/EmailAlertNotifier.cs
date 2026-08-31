using AiObservatory.Data.Repositories;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace AiObservatory.Api.Services;

public sealed class EmailAlertNotifier(ISmtpClient smtpClient, IConfiguration config, IUsageRepository repository)
    : IAlertNotifier
{
    public async Task<AlertDeliveryResult> NotifyAsync(BudgetAlertPayload payload, CancellationToken ct = default)
    {
        var settings = await repository.GetNotificationSettingsAsync(ct);
        var to = settings?.AlertEmailTo;
        if (string.IsNullOrEmpty(to))
        {
            return AlertDeliveryResult.NoRecipientConfigured;
        }

        var host = config["BUDGET_ALERT_SMTP_HOST"] ?? "smtp.office365.com";
        var port = int.TryParse(config["BUDGET_ALERT_SMTP_PORT"], out var p) ? p : 587;
        var user = config["BUDGET_ALERT_SMTP_USER"] ?? string.Empty;
        var pass = config["BUDGET_ALERT_SMTP_PASS"] ?? string.Empty;
        var from = config["BUDGET_ALERT_EMAIL_FROM"] ?? user;

        try
        {
            await smtpClient.ConnectAsync(host, port, SecureSocketOptions.StartTls, ct);
            if (!string.IsNullOrEmpty(user))
            {
                await smtpClient.AuthenticateAsync(user, pass, ct);
            }

            using var message = new MimeMessage();
            message.From.Add(MailboxAddress.Parse(from));
            message.To.Add(MailboxAddress.Parse(to));
            message.MessageId = payload.MessageId;
            message.Subject =
                $"Budget alert: {payload.Provider} {payload.Period} billed spend exceeded £{payload.ThresholdGbp:F2}";
            message.Body = new TextPart("plain")
            {
                Text =
                    $"Total {payload.Period.ToLower()} billed spend for {payload.Provider} reached £{payload.ActualSpendGbp:F2}, "
                    + $"exceeding your £{payload.ThresholdGbp:F2} threshold.\n\nTriggered at: {payload.TriggeredAt:u}",
            };

            await smtpClient.SendAsync(message, ct);
        }
        finally
        {
            if (smtpClient.IsConnected)
            {
                await smtpClient.DisconnectAsync(true, ct);
            }
        }

        return AlertDeliveryResult.Sent;
    }
}
