using AiObservatory.Data.Entities;
using AiObservatory.Data.Repositories;
using AiObservatory.Ingest.Services.Anthropic;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace AiObservatory.Ingest.Tests.Services;

public class AnthropicIngestionServiceTests
{
    private readonly IAnthropicUsageClient _client = Substitute.For<IAnthropicUsageClient>();
    private readonly IUsageRepository _repo = Substitute.For<IUsageRepository>();
    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 6, 2, 10, 0));

    [Fact]
    public async Task IngestAsync_maps_response_to_usage_event_and_aggregate()
    {
        var date = new LocalDate(2026, 6, 1);
        _client.GetUsageAsync(date, Arg.Any<CancellationToken>())
            .Returns([new AnthropicUsageRecord(
                Date: date,
                Model: "claude-sonnet-4-6",
                InputTokens: 10_000,
                OutputTokens: 2_000,
                CacheReadTokens: 3_000,
                CacheWriteTokens: 500,
                CostUsd: 0.045m,
                RawJson: "{}")]);

        var sut = new AnthropicIngestionService(_client, _repo, _clock, NullLogger<AnthropicIngestionService>.Instance);
        await sut.IngestAsync(date, TestContext.Current.CancellationToken);

        await _repo.Received(1).RecordEventAsync(
            Arg.Is<UsageEvent>(e =>
                e.Provider == Provider.Anthropic &&
                e.Model == "claude-sonnet-4-6" &&
                e.InputTokens == 10_000 &&
                e.CacheReadTokens == 3_000 &&
                e.CacheWriteTokens == 500 &&
                e.EventKey == "anthropic:2026-06-01:claude-sonnet-4-6"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IngestAsync_handles_empty_response_gracefully()
    {
        var date = new LocalDate(2026, 6, 1);
        _client.GetUsageAsync(date, Arg.Any<CancellationToken>()).Returns([]);

        var sut = new AnthropicIngestionService(_client, _repo, _clock, NullLogger<AnthropicIngestionService>.Instance);
        await sut.IngestAsync(date, TestContext.Current.CancellationToken);

        await _repo.DidNotReceive().RecordEventAsync(Arg.Any<UsageEvent>(), Arg.Any<CancellationToken>());
    }
}
