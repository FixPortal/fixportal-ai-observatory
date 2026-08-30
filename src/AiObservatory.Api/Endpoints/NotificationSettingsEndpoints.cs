using System.Text.Json;
using AiObservatory.Data;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using NodaTime;

namespace AiObservatory.Api.Endpoints;

public static class NotificationSettingsEndpoints
{
    // ReSharper disable once UnusedMethodReturnValue.Global
    public static IEndpointRouteBuilder MapNotificationSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(
            "/notification-settings",
            async (AiObservatoryDbContext db, CancellationToken ct) =>
            {
                var settings = await db.NotificationSettings.AsNoTracking().FirstOrDefaultAsync(ct);
                return Results.Ok(ToResponse(settings?.AlertEmailTo, settings?.SlackWebhookUrl));
            }
        );

        // Body is bound as raw JsonElement, not a fixed record: a field OMITTED from the JSON
        // body must leave that setting unchanged, while a field present as null or "" clears
        // it. A record's default binding cannot distinguish "omitted" from "present but null"
        // -- both collapse to the same C# null -- so presence is checked with TryGetProperty
        // instead. This distinction is load-bearing: the UI can only ever show a MASKED value
        // for an already-configured field, so it cannot resend that field's real value when
        // saving an edit to the OTHER field -- if the endpoint required both fields on every
        // write, editing the email would have no valid value to send for Slack (and vice
        // versa) without either corrupting or silently clearing it.
        app.MapPut(
            "/notification-settings",
            async (JsonElement body, AiObservatoryDbContext db, IClock clock, CancellationToken ct) =>
            {
                if (
                    body.TryGetProperty("alertEmailTo", out var emailProp)
                    && emailProp.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(emailProp.GetString())
                    && !IsValidEmail(emailProp.GetString()!)
                )
                {
                    return Results.BadRequest("alertEmailTo is not a valid email address");
                }

                if (
                    body.TryGetProperty("slackWebhookUrl", out var slackProp)
                    && slackProp.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(slackProp.GetString())
                    && !IsValidSlackWebhookUrl(slackProp.GetString()!)
                )
                {
                    return Results.BadRequest("slackWebhookUrl must start with https://hooks.slack.com/");
                }

                var settings = await db.NotificationSettings.FirstOrDefaultAsync(ct);
                if (settings is null)
                {
                    settings = new Data.Entities.NotificationSettings();
                    db.NotificationSettings.Add(settings);
                }

                if (body.TryGetProperty("alertEmailTo", out var emailField))
                {
                    var value = emailField.ValueKind == JsonValueKind.Null ? null : emailField.GetString();
                    settings.AlertEmailTo = string.IsNullOrWhiteSpace(value) ? null : value;
                }

                if (body.TryGetProperty("slackWebhookUrl", out var slackField))
                {
                    var value = slackField.ValueKind == JsonValueKind.Null ? null : slackField.GetString();
                    settings.SlackWebhookUrl = string.IsNullOrWhiteSpace(value) ? null : value;
                }

                settings.UpdatedAt = clock.GetCurrentInstant();
                await db.SaveChangesAsync(ct);

                return Results.Ok(ToResponse(settings.AlertEmailTo, settings.SlackWebhookUrl));
            }
        );

        return app;
    }

    private static object ToResponse(string? email, string? slackWebhookUrl) =>
        new
        {
            emailConfigured = !string.IsNullOrEmpty(email),
            emailMasked = NotificationMasking.MaskEmail(email),
            slackConfigured = !string.IsNullOrEmpty(slackWebhookUrl),
            slackMasked = NotificationMasking.MaskWebhookUrl(slackWebhookUrl),
        };

    private static bool IsValidEmail(string email)
    {
        // MimeKit's default parsing is lenient: MailboxAddress.Parse("not-an-email") does NOT
        // throw -- it happily accepts a bare word as a local-part-only mailbox with no domain.
        // Requiring an '@' and a non-empty parsed domain closes that gap.
        if (!email.Contains('@'))
        {
            return false;
        }

        try
        {
            var mailbox = MailboxAddress.Parse(email);
            return !string.IsNullOrEmpty(mailbox.Domain);
        }
        catch (ParseException)
        {
            return false;
        }
    }

    private static bool IsValidSlackWebhookUrl(string url) =>
        url.StartsWith("https://hooks.slack.com/", StringComparison.Ordinal);
}

public static class NotificationMasking
{
    public static string? MaskEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return null;
        }

        var at = email.IndexOf('@');
        if (at <= 0)
        {
            return "***";
        }

        var local = email[..at];
        var domain = email[at..];
        var visible = local.Length <= 2 ? local : local[..2];
        return $"{visible}***{domain}";
    }

    public static string? MaskWebhookUrl(string? url) =>
        string.IsNullOrWhiteSpace(url) ? null : "https://hooks.slack.com/services/***";
}
