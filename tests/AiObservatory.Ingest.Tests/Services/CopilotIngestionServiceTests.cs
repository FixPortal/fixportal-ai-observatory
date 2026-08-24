using AiObservatory.Data.Entities;
using AiObservatory.Data.Repositories;
using AiObservatory.Ingest.Services.Copilot;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace AiObservatory.Ingest.Tests.Services;

public class CopilotIngestionServiceTests
{
    private readonly ICopilotUsageClient _client = Substitute.For<ICopilotUsageClient>();
    private readonly IUsageRepository _repo = Substitute.For<IUsageRepository>();
    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 6, 2, 2, 30));

    [Fact]
    public async Task IngestAsync_writes_flat_usage_event_for_subscription_day()
    {
        var date = new LocalDate(2026, 6, 1);
        _client
            .GetDailyUsageAsync(date, Arg.Any<CancellationToken>())
            .Returns(
                new CopilotUsageRecord(
                    Date: date,
                    ActiveUsers: 1,
                    TotalSuggestionsCount: 142,
                    TotalAcceptancesCount: 89,
                    RawJson: "{}"
                )
            );

        var sut = new CopilotIngestionService(_client, _repo, _clock, NullLogger<CopilotIngestionService>.Instance);
        var result = await sut.IngestAsync(date, date, TestContext.Current.CancellationToken);

        await _repo
            .Received(1)
            .RecordEventAsync(
                Arg.Is<UsageEvent>(e =>
                    e != null
                    && e.Provider == Provider.Copilot
                    && e.CostUsd == 0m
                    && e.SourceId == UsageSourceIds.CopilotOrgReport
                    && e.SourceKind == SourceKind.ProviderApi
                    && e.UsageScope == UsageScope.Subscription
                    && e.CostBasis == CostBasis.None
                    && e.ObservedAt == _clock.GetCurrentInstant()
                    && e.EventKey == "copilot:2026-06-01:copilot"
                ),
                Arg.Any<CancellationToken>()
            );
        result.LatestObservationAt.Should().Be(date.AtStartOfDayInZone(DateTimeZone.Utc).ToInstant());
    }

    [Fact]
    public async Task IngestAsync_does_nothing_when_client_returns_null()
    {
        var date = new LocalDate(2026, 6, 1);
        _client
            .GetDailyUsageAsync(Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns((CopilotUsageRecord?)null);

        var sut = new CopilotIngestionService(_client, _repo, _clock, NullLogger<CopilotIngestionService>.Instance);
        var result = await sut.IngestAsync(date, date.PlusDays(1), TestContext.Current.CancellationToken);

        await _repo.DidNotReceive().RecordEventAsync(Arg.Any<UsageEvent>(), Arg.Any<CancellationToken>());
        await _client.Received(1).GetDailyUsageAsync(date, Arg.Any<CancellationToken>());
        await _client.Received(1).GetDailyUsageAsync(date.PlusDays(1), Arg.Any<CancellationToken>());
        result.LatestObservationAt.Should().BeNull();
    }
}
