using System.Net;
using AiObservatory.Ingest.Services.OpenAi;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;

namespace AiObservatory.Ingest.Tests.Services;

public class OpenAiUsageClientTests
{
    private sealed class StubHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            });
    }

    private static readonly OpenAiPricingOptions TestPricing = new()
    {
        Pricing =
        [
            new OpenAiPricingEntry("gpt-4.1", 2.00m, 8.00m, 0.50m),
            new OpenAiPricingEntry("gpt-4.1-mini", 0.40m, 1.60m, 0.10m),
        ],
        FallbackPricing = new PricingRates3(2.50m, 10.00m, 1.25m),
    };

    private static OpenAiUsageClient CreateSut(LocalDate bucketDate, string model)
    {
        var startTime = bucketDate.AtStartOfDayInZone(DateTimeZone.Utc).ToInstant().ToUnixTimeSeconds();
        var endTime = bucketDate.PlusDays(1).AtStartOfDayInZone(DateTimeZone.Utc).ToInstant().ToUnixTimeSeconds();
        var json = $$"""
            {
              "data": [
                {
                  "start_time": {{startTime}},
                  "end_time": {{endTime}},
                  "results": [
                    {
                      "model": "{{model}}",
                      "input_tokens": 1000000,
                      "output_tokens": 1000000,
                      "input_cached_tokens": 0,
                      "num_model_requests": 1
                    }
                  ]
                }
              ],
              "has_more": false,
              "next_page": null
            }
            """;
        var http = new HttpClient(new StubHandler(json)) { BaseAddress = new Uri("https://api.openai.com") };
        return new OpenAiUsageClient(http, NullLogger<OpenAiUsageClient>.Instance, Options.Create(TestPricing));
    }

    [Fact]
    public async Task GetDailyUsageAsync_resolves_longest_matching_prefix()
    {
        var date = new LocalDate(2026, 7, 1);
        var sut = CreateSut(date, "gpt-4.1-mini-2025-04-14");

        var records = await sut.GetDailyUsageAsync(date, TestContext.Current.CancellationToken);

        records.Single().CostUsd.Should().Be(2.0m); // $0.40 input + $1.60 output per 1M tokens (gpt-4.1-mini, not gpt-4.1)
    }

    [Fact]
    public async Task GetDailyUsageAsync_falls_back_to_fallback_pricing_for_unknown_model()
    {
        var date = new LocalDate(2026, 7, 1);
        var sut = CreateSut(date, "gpt-unknown-model");

        var records = await sut.GetDailyUsageAsync(date, TestContext.Current.CancellationToken);

        records.Single().CostUsd.Should().Be(12.5m); // fallback: $2.50 input + $10 output per 1M tokens
    }
}
