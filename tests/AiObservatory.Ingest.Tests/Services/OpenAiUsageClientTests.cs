using System.Net;
using System.Text.Json;
using AiObservatory.Ingest.Services.OpenAi;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;

namespace AiObservatory.Ingest.Tests.Services;

public sealed class OpenAiUsageClientTests : IDisposable
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
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => Task.FromResult(CreateResponse(HttpStatusCode.OK, json));
    }

    // Simulates a buggy/never-resolving OpenAI API: has_more is always true and next_page
    // always advances, so the loop never terminates on its own. Also counts requests so
    // the test can assert the client actually stopped, not merely that it eventually
    // returned (a hang would time out the test instead).
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

    private OpenAiUsageClient CreateSut(LocalDate bucketDate, string model)
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
                      "input_cached_tokens": 400000,
                      "input_cache_write_tokens": 100000,
                      "input_uncached_tokens": 500000,
                      "output_tokens": 500000,
                      "input_text_tokens": 400000,
                      "output_text_tokens": 400000,
                      "input_cached_text_tokens": 300000,
                      "input_audio_tokens": 50000,
                      "input_cached_audio_tokens": 50000,
                      "output_audio_tokens": 50000,
                      "input_image_tokens": 50000,
                      "input_cached_image_tokens": 50000,
                      "output_image_tokens": 50000,
                      "num_model_requests": 5,
                      "project_id": null,
                      "user_id": null,
                      "api_key_id": null,
                      "batch": null,
                      "service_tier": null,
                      "object": "organization.usage.completions.result"
                    }
                  ]
                }
              ],
              "has_more": false,
              "next_page": null
            }
            """;
        var http = new HttpClient(new StubHandler(json)) { BaseAddress = new Uri("https://api.openai.com") };
        _httpClients.Add(http);
        return new OpenAiUsageClient(http, NullLogger<OpenAiUsageClient>.Instance);
    }

    [Fact]
    public async Task GetDailyUsageAsyncReturnsObservedUsageWithoutPricingIt()
    {
        var date = new LocalDate(2026, 7, 1);
        var sut = CreateSut(date, "gpt-4.1-mini-2025-04-14");

        var records = await sut.GetDailyUsageAsync(date, TestContext.Current.CancellationToken);

        records.Single().Model.Should().Be("gpt-4.1-mini-2025-04-14");
        records.Single().InputTokens.Should().Be(500_000);
        records.Single().CachedInputTokens.Should().Be(400_000);
        records.Single().CacheWriteTokens.Should().Be(100_000);
    }

    [Fact]
    public async Task GetDailyUsageAsync_WhenHasMoreNeverResolves_StopsAtMaxPages()
    {
        // Production bug (AIO backlog): the while(hasMore) loop had no page cap, unlike the
        // sibling AnthropicUsageClient (MaxPages=100) — a stuck has_more from a buggy/
        // misbehaving API would loop the whole poll cycle indefinitely. This drives that
        // exact scenario and asserts the client bails out instead of hanging.
        using var handler = new NeverResolvingHandler();
        using var http = new HttpClient(handler, disposeHandler: false);
        http.BaseAddress = new Uri("https://api.openai.com");
        var sut = new OpenAiUsageClient(http, NullLogger<OpenAiUsageClient>.Instance);

        var records = await sut.GetDailyUsageAsync(new LocalDate(2026, 7, 1), TestContext.Current.CancellationToken);

        records.Should().BeEmpty();
        handler.RequestCount.Should().Be(100);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task GetDailyUsageAsync_ThrowsWhenTheProviderReturnsAnError(HttpStatusCode statusCode)
    {
        using var http = new HttpClient(new StatusHandler(statusCode));
        http.BaseAddress = new Uri("https://api.openai.com");
        var sut = new OpenAiUsageClient(http, NullLogger<OpenAiUsageClient>.Instance);

        var act = () => sut.GetDailyUsageAsync(new LocalDate(2026, 7, 1), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task GetDailyUsageAsync_ThrowsWhenUncachedInputTokensAreMissing()
    {
        var json = """
            {
              "data": [
                {
                  "start_time": 1782864000,
                  "end_time": 1782950400,
                  "results": [
                    {
                      "model": "gpt-5.4",
                      "input_tokens": 10,
                      "output_tokens": 10,
                      "input_cached_tokens": 0,
                      "input_cache_write_tokens": 0,
                      "num_model_requests": 1
                    }
                  ]
                }
              ],
              "has_more": false,
              "next_page": null
            }
            """;
        using var http = new HttpClient(new StubHandler(json));
        http.BaseAddress = new Uri("https://api.openai.com");
        var sut = new OpenAiUsageClient(http, NullLogger<OpenAiUsageClient>.Instance);

        var act = () => sut.GetDailyUsageAsync(new LocalDate(2026, 7, 1), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<JsonException>();
    }
}
