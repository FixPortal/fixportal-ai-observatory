using AiObservatory.Data.Entities;
using AiObservatory.Data.Repositories;
using AiObservatory.Ingest.Services.Google;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace AiObservatory.Ingest.Tests.Services;

public class GoogleIngestionServiceTests
{
    private readonly IGoogleBillingClient _client = Substitute.For<IGoogleBillingClient>();
    private readonly IUsageRepository _repo = Substitute.For<IUsageRepository>();
    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 6, 2, 2, 30));

    [Fact]
    public async Task IngestAsync_maps_billing_record_per_service()
    {
        var date = new LocalDate(2026, 6, 1);
        _client
            .GetDailySpendAsync(date, Arg.Any<CancellationToken>())
            .Returns([new GoogleBillingRecord("AI Platform", "gemini-2.5-pro", 2.50m, "{}")]);

        var sut = new GoogleIngestionService(_client, _repo, _clock, NullLogger<GoogleIngestionService>.Instance);
        var result = await sut.IngestAsync(date, date, TestContext.Current.CancellationToken);

        await _repo
            .Received(1)
            .RecordEventAsync(
                Arg.Is<UsageEvent>(e =>
                    e != null
                    && e.Provider == Provider.Google
                    && e.Model == "gemini-2.5-pro"
                    && e.CostUsd == 2.50m
                    && e.OccurredAt == date.AtStartOfDayInZone(DateTimeZone.Utc).ToInstant()
                    && e.IngestedAt == _clock.GetCurrentInstant()
                    && e.InputTokens == 0
                    && e.OutputTokens == 0
                    && e.SourceId == UsageSourceIds.GoogleCloudBillingExport
                    && e.SourceKind == SourceKind.ProviderApi
                    && e.UsageScope == UsageScope.Api
                    && e.CostBasis == CostBasis.Billed
                    && e.ObservedAt == _clock.GetCurrentInstant()
                    && e.EventKey == "google:2026-06-01:gemini-2.5-pro"
                ),
                Arg.Any<CancellationToken>()
            );
        result.LatestObservationAt.Should().Be(date.AtStartOfDayInZone(DateTimeZone.Utc).ToInstant());
    }

    [Fact]
    public async Task IngestAsync_returns_latest_non_empty_billing_date_across_inclusive_range()
    {
        var first = new LocalDate(2026, 6, 1);
        var middle = first.PlusDays(1);
        var last = first.PlusDays(2);
        _client
            .GetDailySpendAsync(first, Arg.Any<CancellationToken>())
            .Returns([new GoogleBillingRecord("AI Platform", "gemini-2.5-pro", 1m, "{}")]);
        _client.GetDailySpendAsync(middle, Arg.Any<CancellationToken>()).Returns([]);
        _client
            .GetDailySpendAsync(last, Arg.Any<CancellationToken>())
            .Returns([
                new GoogleBillingRecord("AI Platform", "gemini-2.5-flash", 2m, "{}"),
                new GoogleBillingRecord("AI Platform", "gemini-2.5-pro", 3m, "{}"),
            ]);

        var sut = new GoogleIngestionService(_client, _repo, _clock, NullLogger<GoogleIngestionService>.Instance);
        var result = await sut.IngestAsync(first, last, TestContext.Current.CancellationToken);

        result.LatestObservationAt.Should().Be(last.AtStartOfDayInZone(DateTimeZone.Utc).ToInstant());
        await _client.Received(1).GetDailySpendAsync(first, Arg.Any<CancellationToken>());
        await _client.Received(1).GetDailySpendAsync(middle, Arg.Any<CancellationToken>());
        await _client.Received(1).GetDailySpendAsync(last, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IngestAsync_handles_empty_billing_response()
    {
        var date = new LocalDate(2026, 6, 1);
        _client.GetDailySpendAsync(Arg.Any<LocalDate>(), Arg.Any<CancellationToken>()).Returns([]);

        var sut = new GoogleIngestionService(_client, _repo, _clock, NullLogger<GoogleIngestionService>.Instance);
        var result = await sut.IngestAsync(date, date.PlusDays(1), TestContext.Current.CancellationToken);

        await _repo.DidNotReceive().RecordEventAsync(Arg.Any<UsageEvent>(), Arg.Any<CancellationToken>());
        await _client.Received(1).GetDailySpendAsync(date, Arg.Any<CancellationToken>());
        await _client.Received(1).GetDailySpendAsync(date.PlusDays(1), Arg.Any<CancellationToken>());
        result.LatestObservationAt.Should().BeNull();
    }
}
