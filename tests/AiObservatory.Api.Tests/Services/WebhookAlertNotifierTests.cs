using AiObservatory.Api.Services;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;

namespace AiObservatory.Api.Tests.Services;

public class WebhookAlertNotifierTests
{
    private static BudgetAlertPayload MakePayload(string provider = "Anthropic") =>
        new(provider, "Daily", 10m, 15m, DateTimeOffset.UtcNow);

    [Fact]
    public async Task NotifyAsync_is_noop_when_url_not_configured()
    {
        var handler = new CapturingHandler();
        var factory = new FakeHttpClientFactory(handler);
        var config = new ConfigurationBuilder().Build();

        var sut = new WebhookAlertNotifier(factory, config);
        await sut.NotifyAsync(MakePayload(), TestContext.Current.CancellationToken);

        handler.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task NotifyAsync_posts_json_to_configured_url()
    {
        const string webhookUrl = "https://hooks.example.com/alert";
        var handler = new CapturingHandler();
        var factory = new FakeHttpClientFactory(handler);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BUDGET_ALERT_WEBHOOK_URL"] = webhookUrl
            })
            .Build();

        var payload = MakePayload("Anthropic");
        var sut = new WebhookAlertNotifier(factory, config);
        await sut.NotifyAsync(payload, TestContext.Current.CancellationToken);

        handler.CallCount.Should().Be(1);
        handler.LastRequestUri.Should().Be(webhookUrl);

        var body = await handler.LastRequestContent!.ReadAsStringAsync();
        body.Should().Contain("Anthropic");
        body.Should().Contain("Daily");
        body.Should().Contain("10");
    }

    // ---------- helpers ----------

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public string? LastRequestUri { get; private set; }
        public HttpContent? LastRequestContent { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequestUri = request.RequestUri?.ToString();
            // Buffer the content so it remains readable after the request is disposed.
            if (request.Content is not null)
            {
                await request.Content.LoadIntoBufferAsync(cancellationToken);
                LastRequestContent = request.Content;
            }
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        }
    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
