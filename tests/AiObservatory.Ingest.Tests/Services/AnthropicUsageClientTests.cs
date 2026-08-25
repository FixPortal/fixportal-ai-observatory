using System.Net;
using System.Text.Json;
using AiObservatory.Ingest.Services.Anthropic;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;

namespace AiObservatory.Ingest.Tests.Services;

public sealed class AnthropicUsageClientTests : IDisposable
{
    private readonly List<HttpClient> _httpClients = [];

    public void Dispose() => _httpClients.ForEach(client => client.Dispose());

    private static HttpResponseMessage CreateResponse(HttpStatusCode statusCode, string? json = null)
    {
        var response = new HttpResponseMessage(statusCode);
        if (json is not null)
        {
            response.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        }

        return response;
    }

    private sealed class StubHandler(string json) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            RequestUri = request.RequestUri;
            return Task.FromResult(CreateResponse(HttpStatusCode.OK, json));
        }
    }

    private sealed class NeverResolvingHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            RequestCount++;
            var json = $$"""
                {
                  "data": [],
                  "has_more": true,
                  "next_page": "page-{{RequestCount}}"
                }
                """;
            return Task.FromResult(CreateResponse(HttpStatusCode.OK, json));
        }
    }

    private sealed class StatusHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => Task.FromResult(CreateResponse(statusCode));
    }

    private (AnthropicUsageClient Client, StubHandler Handler) CreateSut(LocalDate bucketDate, string model)
    {
        var json = $$"""
            {
              "organization_id": "org_test",
              "data": [
                {
                  "starting_at": "{{bucketDate:yyyy-MM-dd}}T00:00:00Z",
                  "ending_at": "{{bucketDate.PlusDays(1):yyyy-MM-dd}}T00:00:00Z",
                  "results": [
                    {
                      "context_window": "0-200k",
                      "inference_geo": "us",
                      "model": "{{model}}",
                      "output_tokens": 700000,
                      "product": "chat",
                      "requests": 2,
                      "server_tool_use": { "web_search_requests": 0 },
                      "service_tier": "batch",
                      "speed": "standard",
                      "uncached_input_tokens": 500000,
                      "cache_read_input_tokens": 300000,
                      "cache_creation": {
                        "ephemeral_5m_input_tokens": 100000,
                        "ephemeral_1h_input_tokens": 200000
                      }
                    }
                  ]
                }
              ],
              "data_refreshed_at": "{{bucketDate.PlusDays(1):yyyy-MM-dd}}T04:00:00Z",
              "has_more": false,
              "next_page": null
            }
            """;
        var handler = new StubHandler(json);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.anthropic.com") };
        _httpClients.Add(http);
        return (new AnthropicUsageClient(http, NullLogger<AnthropicUsageClient>.Instance), handler);
    }

    [Fact]
    public async Task GetUsageAsyncReturnsObservedUsageWithoutPricingIt()
    {
        var date = new LocalDate(2026, 8, 31);
        var (sut, handler) = CreateSut(date, "claude-sonnet-5");

        var records = await sut.GetUsageAsync(date, TestContext.Current.CancellationToken);

        records
            .Single()
            .Should()
            .BeEquivalentTo(
                new
                {
                    Date = date,
                    Model = "claude-sonnet-5",
                    ServiceTier = "batch",
                    InferenceGeo = "us",
                    Speed = "standard",
                    InputTokens = 500_000L,
                    OutputTokens = 700_000L,
                    CacheReadTokens = 300_000L,
                    CacheWrite5mTokens = 100_000L,
                    CacheWrite1hTokens = 200_000L,
                }
            );
        handler.RequestUri!.Query.Should().Contain("group_by[]=service_tier");
        handler.RequestUri.Query.Should().Contain("group_by[]=inference_geo");
        handler.RequestUri.Query.Should().Contain("group_by[]=speed");
    }

    [Fact]
    public async Task GetUsageAsync_WhenHasMoreNeverResolves_StopsAtMaxPages()
    {
        using var handler = new NeverResolvingHandler();
        using var http = new HttpClient(handler, disposeHandler: false);
        http.BaseAddress = new Uri("https://api.anthropic.com");
        var sut = new AnthropicUsageClient(http, NullLogger<AnthropicUsageClient>.Instance);

        var records = await sut.GetUsageAsync(new LocalDate(2026, 7, 1), TestContext.Current.CancellationToken);

        records.Should().BeEmpty();
        handler.RequestCount.Should().Be(100);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task GetUsageAsync_ThrowsWhenTheProviderReturnsAnError(HttpStatusCode statusCode)
    {
        using var http = new HttpClient(new StatusHandler(statusCode));
        http.BaseAddress = new Uri("https://api.anthropic.com");
        var sut = new AnthropicUsageClient(http, NullLogger<AnthropicUsageClient>.Instance);

        var act = () => sut.GetUsageAsync(new LocalDate(2026, 7, 1), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task GetUsageAsync_ThrowsWhenUncachedInputTokensAreMissing()
    {
        var json = """
            {
              "data": [
                {
                  "starting_at": "2026-07-01T00:00:00Z",
                  "ending_at": "2026-07-02T00:00:00Z",
                  "results": [
                    {
                      "model": "claude-sonnet-5",
                      "output_tokens": 10,
                      "cache_read_input_tokens": 0,
                      "cache_creation": {
                        "ephemeral_5m_input_tokens": 0,
                        "ephemeral_1h_input_tokens": 0
                      },
                      "service_tier": "standard",
                      "inference_geo": "global",
                      "speed": "standard"
                    }
                  ]
                }
              ],
              "has_more": false,
              "next_page": null
            }
            """;
        using var http = new HttpClient(new StubHandler(json));
        http.BaseAddress = new Uri("https://api.anthropic.com");
        var sut = new AnthropicUsageClient(http, NullLogger<AnthropicUsageClient>.Instance);

        var act = () => sut.GetUsageAsync(new LocalDate(2026, 7, 1), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<JsonException>();
    }
}
