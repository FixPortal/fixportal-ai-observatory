using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Configuration;
using MimeKit;

namespace AiObservatory.Api.Services;

public sealed class EmailAlertNotifier(ISmtpClient smtpClient, IConfiguration config) : IAlertNotifier
{
    public async Task NotifyAsync(BudgetAlertPayload payload, CancellationToken ct = default)
    {
        var to = config["BUDGET_ALERT_EMAIL_TO"];
        if (string.IsNullOrEmpty(to)) return;

        var host = config["BUDGET_ALERT_SMTP_HOST"] ?? "smtp.office365.com";
        var port = int.TryParse(config["BUDGET_ALERT_SMTP_PORT"], out var p) ? p : 587;
        var user = config["BUDGET_ALERT_SMTP_USER"] ?? string.Empty;
        var pass = config["BUDGET_ALERT_SMTP_PASS"] ?? string.Empty;
        var from = config["BUDGET_ALERT_EMAIL_FROM"] ?? user;

        await smtpClient.ConnectAsync(host, port, SecureSocketOptions.StartTls, ct);
        if (!string.IsNullOrEmpty(user))
            await smtpClient.AuthenticateAsync(user, pass, ct);

        using var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(from));
        message.To.Add(MailboxAddress.Parse(to));
        message.Subject = $"Budget alert: {payload.Provider} {payload.Period} spend exceeded ${payload.ThresholdUsd:F2}";
        message.Body = new TextPart("plain")
        {
            Text = $"Total {payload.Period.ToLower()} spend for {payload.Provider} reached ${payload.ActualSpend:F2}, " +
                   $"exceeding your ${payload.ThresholdUsd:F2} threshold.\n\nTriggered at: {payload.TriggeredAt:u}"
        };

        await smtpClient.SendAsync(message, ct);
        await smtpClient.DisconnectAsync(true, ct);
    }
}
