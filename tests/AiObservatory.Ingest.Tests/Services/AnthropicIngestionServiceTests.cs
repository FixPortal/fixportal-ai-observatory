using AiObservatory.Data.Entities;
using AiObservatory.Data.Repositories;
using AiObservatory.Ingest.Services.Anthropic;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace AiObservatory.Ingest.Tests.Services;

[Collection("ProviderPollingWorker")]
public class AnthropicIngestionServiceTests
{
    private readonly IAnthropicUsageClient _client = Substitute.For<IAnthropicUsageClient>();
    private readonly IUsageRepository _repo = Substitute.For<IUsageRepository>();
    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 6, 2, 10, 0));

    [Fact]
    public async Task IngestAsync_maps_response_to_usage_event_and_aggregate()
    {
        var date = new LocalDate(2026, 6, 1);
        _client
            .GetUsageAsync(date, Arg.Any<CancellationToken>())
            .Returns([
                new AnthropicUsageRecord(
                    Date: date,
                    Model: "claude-sonnet-4-6",
                    ServiceTier: "batch",
                    InferenceGeo: "us",
                    Speed: "standard",
                    InputTokens: 10_000,
                    OutputTokens: 2_000,
                    CacheReadTokens: 3_000,
                    CacheWrite5mTokens: 200,
                    CacheWrite1hTokens: 300,
                    RawJson: """{"service_tier":"batch","inference_geo":"us","speed":"standard"}"""
                ),
                new AnthropicUsageRecord(
                    Date: date,
                    Model: "claude-sonnet-4-6",
                    ServiceTier: "standard",
                    InferenceGeo: "global",
                    Speed: "fast",
                    InputTokens: 20_000,
                    OutputTokens: 4_000,
                    CacheReadTokens: 6_000,
                    CacheWrite5mTokens: 400,
                    CacheWrite1hTokens: 600,
                    RawJson: """{"service_tier":"standard","inference_geo":"global","speed":"fast"}"""
                ),
            ]);

        var sut = new AnthropicIngestionService(_client, _repo, _clock, NullLogger<AnthropicIngestionService>.Instance);
        var result = await sut.IngestAsync(date, date, TestContext.Current.CancellationToken);

        await _repo
            .Received(1)
            .RecordEstimatedEventAsync(
                Arg.Is<UsageEvent>(e =>
                    e != null
                    && e.Provider == Provider.Anthropic
                    && e.Model == "claude-sonnet-4-6"
                    && e.InputTokens == 10_000
                    && e.CacheReadTokens == 3_000
                    && e.CacheWriteTokens == 500
                    && e.CacheWrite1hTokens == 300
                    && e.SourceId == UsageSourceIds.AnthropicUsageApi
                    && e.SourceKind == SourceKind.ProviderApi
                    && e.UsageScope == UsageScope.Api
                    && e.CostBasis == CostBasis.ListPriceEstimate
                    && e.CostUsd == null
                    && e.ObservedAt == _clock.GetCurrentInstant()
                    && e.EventKey == "anthropic:2026-06-01:claude-sonnet-4-6:batch:us:standard"
                    && e.RawPayload.Contains("\"service_tier\":\"batch\"", StringComparison.Ordinal)
                    && e.RawPayload.Contains("\"ephemeral_1h_input_tokens\":300", StringComparison.Ordinal)
                ),
                Arg.Any<CancellationToken>()
            );
        await _repo
            .Received(1)
            .RecordEstimatedEventAsync(
                Arg.Is<UsageEvent>(e =>
                    e != null
                    && e.InputTokens == 20_000
                    && e.CacheWriteTokens == 1_000
                    && e.CacheWrite1hTokens == 600
                    && e.EventKey == "anthropic:2026-06-01:claude-sonnet-4-6:standard:global:fast"
                ),
                Arg.Any<CancellationToken>()
            );
        result.LatestObservationAt.Should().Be(date.AtStartOfDayInZone(DateTimeZone.Utc).ToInstant());
    }

    [Fact]
    public async Task IngestAsync_handles_empty_response_gracefully()
    {
        var date = new LocalDate(2026, 6, 1);
        _client.GetUsageAsync(Arg.Any<LocalDate>(), Arg.Any<CancellationToken>()).Returns([]);

        var sut = new AnthropicIngestionService(_client, _repo, _clock, NullLogger<AnthropicIngestionService>.Instance);
        var result = await sut.IngestAsync(date, date.PlusDays(1), TestContext.Current.CancellationToken);

        await _repo.DidNotReceive().RecordEstimatedEventAsync(Arg.Any<UsageEvent>(), Arg.Any<CancellationToken>());
        await _client.Received(1).GetUsageAsync(date, Arg.Any<CancellationToken>());
        await _client.Received(1).GetUsageAsync(date.PlusDays(1), Arg.Any<CancellationToken>());
        result.LatestObservationAt.Should().BeNull();
    }
}
