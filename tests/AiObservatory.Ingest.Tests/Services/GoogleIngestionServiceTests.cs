using AiObservatory.Data.Entities;
using AiObservatory.Data.Repositories;
using AiObservatory.Ingest.Services.Google;
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
        _client.GetDailySpendAsync(date, Arg.Any<CancellationToken>())
            .Returns([new GoogleBillingRecord("AI Platform", "gemini-2.5-pro", 2.50m, "{}")]);

        var sut = new GoogleIngestionService(_client, _repo, _clock, NullLogger<GoogleIngestionService>.Instance);
        await sut.IngestAsync(date, TestContext.Current.CancellationToken);

        await _repo.Received(1).RecordEventAsync(
            Arg.Is<UsageEvent>(e =>
                e.Provider == Provider.Google &&
                e.Model == "gemini-2.5-pro" &&
                e.CostUsd == 2.50m &&
                e.OccurredAt == date.AtStartOfDayInZone(DateTimeZone.Utc).ToInstant() &&
                e.IngestedAt == _clock.GetCurrentInstant() &&
                e.InputTokens == 0 &&
                e.OutputTokens == 0 &&
                e.EventKey == "google:2026-06-01:gemini-2.5-pro"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IngestAsync_handles_empty_billing_response()
    {
        var date = new LocalDate(2026, 6, 1);
        _client.GetDailySpendAsync(date, Arg.Any<CancellationToken>()).Returns([]);

        var sut = new GoogleIngestionService(_client, _repo, _clock, NullLogger<GoogleIngestionService>.Instance);
        await sut.IngestAsync(date, TestContext.Current.CancellationToken);

        await _repo.DidNotReceive().RecordEventAsync(Arg.Any<UsageEvent>(), Arg.Any<CancellationToken>());
    }
}
