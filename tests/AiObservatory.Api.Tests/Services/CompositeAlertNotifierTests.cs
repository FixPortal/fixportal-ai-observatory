using AiObservatory.Api.Services;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace AiObservatory.Api.Tests.Services;

public class CompositeAlertNotifierTests
{
    private static BudgetAlertPayload MakePayload() =>
        new(
            "Anthropic",
            "Daily",
            10m,
            15m,
            DateTimeOffset.UtcNow,
            "budget-alert-10000000000000000000000000000001@observatory.fixportal.com",
            Guid.NewGuid()
        );

    [Fact]
    public async Task NotifyAsync_calls_both_channels()
    {
        var email = Substitute.For<IAlertNotifier>();
        var slack = Substitute.For<IAlertNotifier>();
        var sut = new CompositeAlertNotifier(email, slack, NullLogger<CompositeAlertNotifier>.Instance);

        await sut.NotifyAsync(MakePayload(), TestContext.Current.CancellationToken);

        await email.Received(1).NotifyAsync(Arg.Any<BudgetAlertPayload>(), Arg.Any<CancellationToken>());
        await slack.Received(1).NotifyAsync(Arg.Any<BudgetAlertPayload>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NotifyAsync_still_calls_email_when_slack_throws()
    {
        var email = Substitute.For<IAlertNotifier>();
        var slack = Substitute.For<IAlertNotifier>();
        slack
            .NotifyAsync(Arg.Any<BudgetAlertPayload>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("boom"));
        var sut = new CompositeAlertNotifier(email, slack, NullLogger<CompositeAlertNotifier>.Instance);

        var act = async () => await sut.NotifyAsync(MakePayload(), TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync();
        await email.Received(1).NotifyAsync(Arg.Any<BudgetAlertPayload>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(AlertDeliveryResult.Sent, AlertDeliveryResult.Sent, AlertDeliveryResult.Sent)]
    [InlineData(AlertDeliveryResult.Failed, AlertDeliveryResult.Sent, AlertDeliveryResult.Sent)]
    [InlineData(AlertDeliveryResult.NoRecipientConfigured, AlertDeliveryResult.Sent, AlertDeliveryResult.Sent)]
    [InlineData(AlertDeliveryResult.Sent, AlertDeliveryResult.NoRecipientConfigured, AlertDeliveryResult.Sent)]
    [InlineData(AlertDeliveryResult.Failed, AlertDeliveryResult.NoRecipientConfigured, AlertDeliveryResult.Failed)]
    [InlineData(
        AlertDeliveryResult.NoRecipientConfigured,
        AlertDeliveryResult.NoRecipientConfigured,
        AlertDeliveryResult.NoRecipientConfigured
    )]
    public async Task NotifyAsync_reports_sent_only_when_a_channel_actually_delivered(
        AlertDeliveryResult slackResult,
        AlertDeliveryResult emailResult,
        AlertDeliveryResult expected
    )
    {
        var email = Substitute.For<IAlertNotifier>();
        var slack = Substitute.For<IAlertNotifier>();
        slack.NotifyAsync(Arg.Any<BudgetAlertPayload>(), Arg.Any<CancellationToken>()).Returns(slackResult);
        email.NotifyAsync(Arg.Any<BudgetAlertPayload>(), Arg.Any<CancellationToken>()).Returns(emailResult);
        var sut = new CompositeAlertNotifier(email, slack, NullLogger<CompositeAlertNotifier>.Instance);

        var result = await sut.NotifyAsync(MakePayload(), TestContext.Current.CancellationToken);

        result.Should().Be(expected);
    }

    [Fact]
    public async Task NotifyAsync_propagates_when_email_throws_preserving_the_existing_retry_contract()
    {
        var email = Substitute.For<IAlertNotifier>();
        var slack = Substitute.For<IAlertNotifier>();
        email
            .NotifyAsync(Arg.Any<BudgetAlertPayload>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("smtp down"));
        var sut = new CompositeAlertNotifier(email, slack, NullLogger<CompositeAlertNotifier>.Instance);

        var act = async () => await sut.NotifyAsync(MakePayload(), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
