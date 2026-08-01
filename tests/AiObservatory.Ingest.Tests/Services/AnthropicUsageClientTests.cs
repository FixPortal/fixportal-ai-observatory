using System.Net;
using System.Text.Json;
using AiObservatory.Data.Pricing;
using AiObservatory.Ingest.Services.Anthropic;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;

namespace AiObservatory.Ingest.Tests.Services;

public class AnthropicUsageClientTests
{
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
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => Task.FromResult(CreateResponse(HttpStatusCode.OK, json));
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

    private static readonly AnthropicPricingOptions TestPricing = new()
    {
        Pricing =
        [
            new AnthropicPricingEntry(
                "claude-sonnet-5",
                2.0m,
                10.0m,
                0.20m,
                2.50m,
                4.00m,
                EffectiveTo: new LocalDate(2026, 8, 31)
            ),
            new AnthropicPricingEntry(
                "claude-sonnet-5",
                3.0m,
                15.0m,
                0.30m,
                3.75m,
                6.00m,
                EffectiveFrom: new LocalDate(2026, 9, 1)
            ),
        ],
        FallbackPricing = new PricingRates(3.0m, 15.0m, 0.30m, 3.75m, 6.00m),
    };

    private static AnthropicUsageClient CreateSut(LocalDate bucketDate, string model)
    {
        var json = $$"""
            {
              "data": [
                {
                  "starting_at": "{{bucketDate:yyyy-MM-dd}}T00:00:00Z",
                  "ending_at": "{{bucketDate.PlusDays(1):yyyy-MM-dd}}T00:00:00Z",
                  "results": [
                    {
                      "model": "{{model}}",
                      "input_tokens": 1000000,
                      "output_tokens": 1000000,
                      "cache_read_input_tokens": 0,
                      "cache_creation_input_tokens": 0
                    }
                  ]
                }
              ],
              "has_more": false,
              "next_page": null
            }
            """;
        var http = new HttpClient(new StubHandler(json)) { BaseAddress = new Uri("https://api.anthropic.com") };
        return new AnthropicUsageClient(http, NullLogger<AnthropicUsageClient>.Instance, Options.Create(TestPricing));
    }

    [Fact]
    public async Task GetUsageAsync_applies_sonnet5_intro_rate_within_window()
    {
        var date = new LocalDate(2026, 8, 31);
        var sut = CreateSut(date, "claude-sonnet-5");

        var records = await sut.GetUsageAsync(date, TestContext.Current.CancellationToken);

        records.Single().CostUsd.Should().Be(12.0m); // $2 input + $10 output per 1M tokens
    }

    [Fact]
    public async Task GetUsageAsync_applies_standard_rate_after_intro_window()
    {
        var date = new LocalDate(2026, 9, 1);
        var sut = CreateSut(date, "claude-sonnet-5");

        var records = await sut.GetUsageAsync(date, TestContext.Current.CancellationToken);

        records.Single().CostUsd.Should().Be(18.0m); // $3 input + $15 output per 1M tokens
    }

    [Fact]
    public async Task GetUsageAsync_falls_back_to_fallback_pricing_for_unknown_model()
    {
        var date = new LocalDate(2026, 7, 1);
        var sut = CreateSut(date, "claude-unknown-model");

        var records = await sut.GetUsageAsync(date, TestContext.Current.CancellationToken);

        records.Single().CostUsd.Should().Be(18.0m); // fallback: $3 input + $15 output per 1M tokens
    }

    [Fact]
    public async Task GetUsageAsync_WhenHasMoreNeverResolves_StopsAtMaxPages()
    {
        using var handler = new NeverResolvingHandler();
        using var http = new HttpClient(handler, disposeHandler: false);
        http.BaseAddress = new Uri("https://api.anthropic.com");
        var sut = new AnthropicUsageClient(
            http,
            NullLogger<AnthropicUsageClient>.Instance,
            Options.Create(TestPricing)
        );

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
        var sut = new AnthropicUsageClient(
            http,
            NullLogger<AnthropicUsageClient>.Instance,
            Options.Create(TestPricing)
        );

        var act = () => sut.GetUsageAsync(new LocalDate(2026, 7, 1), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task GetUsageAsync_ThrowsWhenInputTokensAreMissing()
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
                      "cache_creation_input_tokens": 0
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
        var sut = new AnthropicUsageClient(
            http,
            NullLogger<AnthropicUsageClient>.Instance,
            Options.Create(TestPricing)
        );

        var act = () => sut.GetUsageAsync(new LocalDate(2026, 7, 1), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<JsonException>();
    }
}
