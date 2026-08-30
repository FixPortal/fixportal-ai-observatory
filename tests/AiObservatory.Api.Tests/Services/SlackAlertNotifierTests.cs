using System.Net;
using System.Text.Json;
using AiObservatory.Api.Services;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Repositories;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;

namespace AiObservatory.Api.Tests.Services;

public class SlackAlertNotifierTests
{
    // ponytail: StubHttpMessageHandler (shared with FxRateProviderTests/GitHubBillingClientTests)
    // takes a fixed (status, body) pair and only records request URIs, not bodies or
    // per-call responses -- doesn't fit needing both a captured JSON body and dynamic status
    // per test, so this is a small local double instead of extending the shared one.
    private sealed class CapturingHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            if (request.Content is not null)
            {
                // Force the content to be buffered before the request object is handed back,
                // since HttpClient may dispose/consume the stream after SendAsync returns.
                var bytes = await request.Content.ReadAsByteArrayAsync(cancellationToken);
                request.Content = new ByteArrayContent(bytes);
                request.Content.Headers.ContentType = new("application/json");
            }

            Requests.Add(request);
            return new HttpResponseMessage(status);
        }
    }

    private static BudgetAlertPayload MakePayload() =>
        new(
            "Anthropic",
            "Daily",
            10m,
            15m,
            DateTimeOffset.UtcNow,
            "budget-alert-10000000000000000000000000000001@observatory.fixportal.com"
        );

    [Fact]
    public async Task NotifyAsync_is_noop_when_webhook_not_configured()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK);
        var http = new HttpClient(handler);
        var repo = Substitute.For<IUsageRepository>();
        repo.GetNotificationSettingsAsync(Arg.Any<CancellationToken>()).Returns((NotificationSettings?)null);

        var sut = new SlackAlertNotifier(http, repo, NullLogger<SlackAlertNotifier>.Instance);
        await sut.NotifyAsync(MakePayload(), TestContext.Current.CancellationToken);

        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task NotifyAsync_posts_a_text_payload_to_the_configured_webhook()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK);
        var http = new HttpClient(handler);
        var repo = Substitute.For<IUsageRepository>();
        repo.GetNotificationSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(
                new NotificationSettings
                {
                    SlackWebhookUrl = "https://hooks.slack.com/services/T0/B0/xyz",
                    UpdatedAt = Instant.FromUtc(2026, 8, 30, 0, 0),
                }
            );

        var sut = new SlackAlertNotifier(http, repo, NullLogger<SlackAlertNotifier>.Instance);
        await sut.NotifyAsync(MakePayload(), TestContext.Current.CancellationToken);

        handler.Requests.Should().ContainSingle();
        var request = handler.Requests[0];
        request.RequestUri.Should().Be(new Uri("https://hooks.slack.com/services/T0/B0/xyz"));
        request.Content.Should().NotBeNull();
        var body = await request.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var json = JsonSerializer.Deserialize<JsonElement>(body);
        json.GetProperty("text").GetString().Should().Contain("Anthropic").And.Contain("£10.00");
    }

    [Fact]
    public async Task NotifyAsync_does_not_throw_when_the_webhook_call_fails()
    {
        var handler = new CapturingHandler(HttpStatusCode.InternalServerError);
        var http = new HttpClient(handler);
        var repo = Substitute.For<IUsageRepository>();
        repo.GetNotificationSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(
                new NotificationSettings
                {
                    SlackWebhookUrl = "https://hooks.slack.com/services/T0/B0/xyz",
                    UpdatedAt = Instant.FromUtc(2026, 8, 30, 0, 0),
                }
            );

        var sut = new SlackAlertNotifier(http, repo, NullLogger<SlackAlertNotifier>.Instance);
        var act = async () => await sut.NotifyAsync(MakePayload(), TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync();
    }
}
