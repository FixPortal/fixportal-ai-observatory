using AiObservatory.Api.Services;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Repositories;
using AwesomeAssertions;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Configuration;
using MimeKit;
using NodaTime;
using NSubstitute;

namespace AiObservatory.Api.Tests.Services;

public class EmailAlertNotifierTests
{
    private static BudgetAlertPayload MakePayload(string provider = "Anthropic") =>
        new(
            provider,
            "Daily",
            10m,
            15m,
            DateTimeOffset.UtcNow,
            "budget-alert-10000000000000000000000000000001@observatory.fixportal.com",
            Guid.NewGuid()
        );

    [Fact]
    public async Task NotifyAsync_is_noop_when_no_settings_row_exists()
    {
        var smtp = Substitute.For<ISmtpClient>();
        var config = new ConfigurationBuilder().Build();
        var repo = Substitute.For<IUsageRepository>();
        repo.GetNotificationSettingsAsync(Arg.Any<CancellationToken>()).Returns((NotificationSettings?)null);

        var sut = new EmailAlertNotifier(smtp, config, repo);
        var result = await sut.NotifyAsync(MakePayload(), TestContext.Current.CancellationToken);

        result.Should().Be(AlertDeliveryResult.NoRecipientConfigured);
        await smtp.DidNotReceive()
            .ConnectAsync(
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<SecureSocketOptions>(),
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task NotifyAsync_is_noop_when_recipient_is_unset_on_the_row()
    {
        var smtp = Substitute.For<ISmtpClient>();
        var config = new ConfigurationBuilder().Build();
        var repo = Substitute.For<IUsageRepository>();
        repo.GetNotificationSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(new NotificationSettings { AlertEmailTo = null, UpdatedAt = Instant.FromUtc(2026, 8, 30, 0, 0) });

        var sut = new EmailAlertNotifier(smtp, config, repo);
        var result = await sut.NotifyAsync(MakePayload(), TestContext.Current.CancellationToken);

        result.Should().Be(AlertDeliveryResult.NoRecipientConfigured);
        await smtp.DidNotReceive()
            .ConnectAsync(
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<SecureSocketOptions>(),
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task NotifyAsync_connects_authenticates_and_sends_when_configured()
    {
        var smtp = Substitute.For<ISmtpClient>();
        smtp.IsConnected.Returns(true);
        MimeMessage? sent = null;
        smtp.When(x => x.SendAsync(Arg.Any<MimeMessage>(), Arg.Any<CancellationToken>(), Arg.Any<ITransferProgress>()))
            .Do(x => sent = x.Arg<MimeMessage>());

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["BUDGET_ALERT_EMAIL_FROM"] = "obs@example.com",
                    ["BUDGET_ALERT_SMTP_HOST"] = "smtp.example.com",
                    ["BUDGET_ALERT_SMTP_USER"] = "obs@example.com",
                    ["BUDGET_ALERT_SMTP_PASS"] = "secret",
                }
            )
            .Build();
        var repo = Substitute.For<IUsageRepository>();
        repo.GetNotificationSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(
                new NotificationSettings
                {
                    AlertEmailTo = "alerts@example.com",
                    UpdatedAt = Instant.FromUtc(2026, 8, 30, 0, 0),
                }
            );

        var sut = new EmailAlertNotifier(smtp, config, repo);
        var result = await sut.NotifyAsync(MakePayload(), TestContext.Current.CancellationToken);

        result.Should().Be(AlertDeliveryResult.Sent);
        await smtp.Received(1)
            .ConnectAsync("smtp.example.com", 587, SecureSocketOptions.StartTls, Arg.Any<CancellationToken>());
        await smtp.Received(1).AuthenticateAsync("obs@example.com", "secret", Arg.Any<CancellationToken>());
        await smtp.Received(1).DisconnectAsync(true, Arg.Any<CancellationToken>());

        sent.Should().NotBeNull();
        sent.MessageId.Should().Be(MakePayload().MessageId);
        sent.Subject.Should().Contain("Anthropic").And.Contain("billed spend").And.Contain("£10.00");
        sent.To.ToString().Should().Contain("alerts@example.com");
    }
}
