using AiObservatory.Data;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Pricing;
using AiObservatory.Data.Repositories;
using AiObservatory.Ingest.Services.OpenAi;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace AiObservatory.Ingest.Tests.Services;

[Collection("ProviderPollingWorker")]
public class OpenAiIngestionServiceTests(ProviderPollingDatabase database)
{
    [Fact]
    public async Task IngestAsyncGroupsAProviderDayByModelAndBuildsStableEventKeys()
    {
        var date = new LocalDate(2026, 7, 29);
        var client = Substitute.For<IOpenAiUsageClient>();
        client
            .GetDailyUsageAsync(date, Arg.Any<CancellationToken>())
            .Returns(
                new List<OpenAiUsageRecord>
                {
                    new(date, "gpt-5.4", 10, 5, 2, """{"id":1}"""),
                    new(date, "gpt-5.4", 20, 7, 3, """{"id":2}"""),
                    new(date, "o4-mini", 4, 2, 1, """{"id":3}"""),
                }
            );
        var recorded = new List<UsageEvent>();
        var repository = Substitute.For<IUsageRepository>();
        repository
            .RecordEventAsync(Arg.Any<UsageEvent>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var evt = call.Arg<UsageEvent>()!;
                recorded.Add(evt);
                return new RecordEventResult(evt.Id, RecordEventDisposition.Created);
            });
        await using var db = CreateDb();
        var sut = new OpenAiIngestionService(
            client,
            repository,
            Resolver(db),
            new FakeClock(Instant.FromUtc(2026, 7, 30, 9, 0)),
            NullLogger<OpenAiIngestionService>.Instance
        );

        var result = await sut.IngestAsync(date, date, TestContext.Current.CancellationToken);

        recorded.Should().HaveCount(2);
        recorded
            .Should()
            .ContainEquivalentOf(
                new
                {
                    Provider = Provider.OpenAI,
                    Model = "gpt-5.4",
                    InputTokens = 30L,
                    OutputTokens = 12L,
                    CacheReadTokens = (long?)5,
                    CostUsd = (decimal?)null,
                    SourceId = UsageSourceIds.OpenAiUsageApi,
                    SourceKind = SourceKind.ProviderApi,
                    UsageScope = UsageScope.Api,
                    CostBasis = CostBasis.ListPriceEstimate,
                    ObservedAt = Instant.FromUtc(2026, 7, 30, 9, 0),
                    EventKey = "openai:2026-07-29:gpt-5.4",
                    RawPayload = """[{"id":1},{"id":2}]""",
                }
            );
        recorded
            .Should()
            .ContainEquivalentOf(
                new
                {
                    Provider = Provider.OpenAI,
                    Model = "o4-mini",
                    EventKey = "openai:2026-07-29:o4-mini",
                }
            );
        result.LatestObservationAt.Should().Be(date.AtStartOfDayInZone(DateTimeZone.Utc).ToInstant());
    }

    [Fact]
    public async Task IngestAsyncPollsTheInclusiveRange()
    {
        var from = new LocalDate(2026, 7, 28);
        var through = new LocalDate(2026, 7, 29);
        var client = Substitute.For<IOpenAiUsageClient>();
        client.GetDailyUsageAsync(Arg.Any<LocalDate>(), Arg.Any<CancellationToken>()).Returns([]);
        await using var db = CreateDb();
        var sut = new OpenAiIngestionService(
            client,
            Substitute.For<IUsageRepository>(),
            Resolver(db),
            new FakeClock(Instant.FromUtc(2026, 7, 30, 9, 0)),
            NullLogger<OpenAiIngestionService>.Instance
        );

        var result = await sut.IngestAsync(from, through, TestContext.Current.CancellationToken);

        await client.Received(1).GetDailyUsageAsync(from, Arg.Any<CancellationToken>());
        await client.Received(1).GetDailyUsageAsync(through, Arg.Any<CancellationToken>());
        result.LatestObservationAt.Should().BeNull();
    }

    private AiObservatoryDbContext CreateDb() =>
        new(
            new DbContextOptionsBuilder<AiObservatoryDbContext>()
                .UseNpgsql(database.ConnectionString, npgsql => npgsql.UseNodaTime())
                .Options
        );

    private static UsagePriceResolver Resolver(AiObservatoryDbContext db) =>
        new(new PricingSnapshotStore(db), [new OpenAiPriceCalculator()], NullLogger<UsagePriceResolver>.Instance);
}
