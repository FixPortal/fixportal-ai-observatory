using AiObservatory.Api.Services;
using AwesomeAssertions;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Configuration;
using MimeKit;
using NSubstitute;

namespace AiObservatory.Api.Tests.Services;

public class EmailAlertNotifierTests
{
    private static BudgetAlertPayload MakePayload(string provider = "Anthropic") =>
        new(provider, "Daily", 10m, 15m, DateTimeOffset.UtcNow);

    [Fact]
    public async Task NotifyAsync_is_noop_when_email_not_configured()
    {
        var smtp = Substitute.For<ISmtpClient>();
        var config = new ConfigurationBuilder().Build();

        var sut = new EmailAlertNotifier(smtp, config);
        await sut.NotifyAsync(MakePayload(), TestContext.Current.CancellationToken);

        await smtp.DidNotReceive().ConnectAsync(
            Arg.Any<string>(), Arg.Any<int>(),
            Arg.Any<SecureSocketOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NotifyAsync_connects_authenticates_and_sends_when_configured()
    {
        var smtp = Substitute.For<ISmtpClient>();
        // The notifier only disconnects when still connected; mirror a live client that
        // reports connected after ConnectAsync so the finally-block disconnect runs.
        smtp.IsConnected.Returns(true);
        MimeMessage? sent = null;
        smtp.When(x => x.SendAsync(Arg.Any<MimeMessage>(), Arg.Any<CancellationToken>(), Arg.Any<ITransferProgress>()))
            .Do(x => sent = x.Arg<MimeMessage>());

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BUDGET_ALERT_EMAIL_TO"] = "alerts@example.com",
                ["BUDGET_ALERT_EMAIL_FROM"] = "obs@example.com",
                ["BUDGET_ALERT_SMTP_HOST"] = "smtp.example.com",
                ["BUDGET_ALERT_SMTP_USER"] = "obs@example.com",
                ["BUDGET_ALERT_SMTP_PASS"] = "secret",
            })
            .Build();

        var sut = new EmailAlertNotifier(smtp, config);
        await sut.NotifyAsync(MakePayload("Anthropic"), TestContext.Current.CancellationToken);

        await smtp.Received(1).ConnectAsync("smtp.example.com", 587, SecureSocketOptions.StartTls, Arg.Any<CancellationToken>());
        await smtp.Received(1).AuthenticateAsync("obs@example.com", "secret", Arg.Any<CancellationToken>());
        await smtp.Received(1).DisconnectAsync(true, Arg.Any<CancellationToken>());

        sent.Should().NotBeNull();
        sent!.Subject.Should().Contain("Anthropic").And.Contain("10.00");
        sent.To.ToString().Should().Contain("alerts@example.com");
    }
}
