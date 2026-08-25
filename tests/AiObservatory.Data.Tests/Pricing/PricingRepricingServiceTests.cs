using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Pricing;
using AiObservatory.Data.Pricing.Catalogs;
using AiObservatory.Data.Repositories;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Serialization.SystemTextJson;
using Npgsql;

namespace AiObservatory.Data.Tests.Pricing;

[Trait("Category", "Integration")]
public sealed class PricingRepricingServiceTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions(
        JsonSerializerDefaults.Web
    ).ConfigureForNodaTime(DateTimeZoneProviders.Tzdb);
    private AiObservatoryDbContext _db = null!;
    private UsageRepository _repository = null!;
    private PricingSnapshotStore _store = null!;
    private PricingRepricingService _repricing = null!;

    public async ValueTask InitializeAsync()
    {
        var baseConnection =
            Environment.GetEnvironmentVariable("TEST_DB_CONNECTION")
            ?? "Host=localhost;Database=aiobs_test;Username=postgres;Password=postgres";
        var connectionString = new NpgsqlConnectionStringBuilder(baseConnection)
        {
            Database = $"aiobs_test_repricing_{Guid.NewGuid():N}",
        }.ConnectionString;
        var options = new DbContextOptionsBuilder<AiObservatoryDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.UseNodaTime())
            .Options;
        _db = new AiObservatoryDbContext(options);
        await _db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        _repository = new UsageRepository(_db);
        _store = new PricingSnapshotStore(_db);
        var resolver = new UsagePriceResolver(
            _store,
            [new OpenAiPriceCalculator()],
            NullLogger<UsagePriceResolver>.Instance
        );
        _repricing = new PricingRepricingService(_db, _repository, resolver);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _db.Database.EnsureDeletedAsync();
        }
        finally
        {
            await _db.DisposeAsync();
        }
    }

    [Fact]
    public async Task RepricingUpdatesOnlyEligibleEstimatesAndRepairsAggregateCoverage()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.ActivateAsync(Candidate("old", 1m), ct);
        var eligible = Event("known", CostBasis.ListPriceEstimate, 9m, 3m);
        var notional = Event("notional", CostBasis.Notional, 8m, 2m);
        var unknownModel = Event("missing", CostBasis.ListPriceEstimate, 7m, 1m, "unknown-model");
        var billed = Event("billed", CostBasis.Billed, 6m, 1m);
        var providerEstimated = Event("provider", CostBasis.ProviderEstimated, 5m, 1m);
        var legacy = Event("legacy", CostBasis.Unknown, 4m, 1m);
        foreach (var usage in new[] { eligible, notional, unknownModel, billed, providerEstimated, legacy })
        {
            await _repository.RecordEventAsync(usage, ct);
        }

        await _store.ActivateAsync(Candidate("new", 2m), ct);
        await _repricing.RepriceProviderAsync(Provider.OpenAI, ct);

        var saved = await _db.UsageEvents.AsNoTracking().ToDictionaryAsync(row => row.EventKey!, ct);
        saved["known"].CostUsd.Should().Be(2m);
        saved["known"].CacheSavingsUsd.Should().Be(0m);
        saved["notional"].CostUsd.Should().Be(2m);
        saved["missing"].CostUsd.Should().BeNull();
        saved["missing"].CacheSavingsUsd.Should().BeNull();
        saved["billed"].CostUsd.Should().Be(6m);
        saved["provider"].CostUsd.Should().Be(5m);
        saved["legacy"].CostUsd.Should().Be(4m);

        var aggregate = await _db
            .DailyAggregates.AsNoTracking()
            .SingleAsync(row => row.Model == "unknown-model" && row.CostBasis == CostBasis.ListPriceEstimate, ct);
        aggregate.CostUsd.Should().Be(0m);
        aggregate.UnknownCostCount.Should().Be(1);
        aggregate.CacheSavingsUsd.Should().Be(0m);
        aggregate.UnknownCacheSavingsCount.Should().Be(1);
    }

    [Fact]
    public async Task ActivationCallbackCommitsEffectiveDateRepricingWithTheSnapshot()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.ActivateAsync(Candidate("old", 1m), ct);
        await _repository.RecordEventAsync(Event("before", CostBasis.ListPriceEstimate, 1m, 0m), ct);
        await _repository.RecordEventAsync(
            Event("after", CostBasis.ListPriceEstimate, 1m, 0m, occurredAt: Instant.FromUtc(2026, 9, 2, 0, 0)),
            ct
        );

        await _store.ActivateAsync(
            Candidate("windowed", 2m, (new LocalDate(2026, 9, 1), 3m)),
            ct,
            (_, callbackCt) => _repricing.RepriceProviderAsync(Provider.OpenAI, callbackCt)
        );

        var costs = await _db
            .UsageEvents.AsNoTracking()
            .OrderBy(row => row.EventKey)
            .Select(row => row.CostUsd)
            .ToListAsync(ct);
        costs.Should().Equal(3m, 2m);
        (await _store.GetActiveAsync(PricingSourceIds.OpenAi, ct))!.RawEvidence.Should().Be("windowed");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActivationCallbackFailureRollsBackSnapshotEventsAndAggregates(bool cancel)
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.ActivateAsync(Candidate("old", 1m), ct);
        await _repository.RecordEventAsync(Event("event", CostBasis.ListPriceEstimate, 1m, 0m), ct);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var activationToken = cancel ? cancellation.Token : ct;

        var activation = async () =>
            await _store.ActivateAsync(
                Candidate("new", 2m),
                activationToken,
                async (_, callbackCt) =>
                {
                    await _repricing.RepriceProviderAsync(Provider.OpenAI, callbackCt);
                    if (cancel)
                    {
                        await cancellation.CancelAsync();
                        callbackCt.ThrowIfCancellationRequested();
                    }

                    throw new InvalidOperationException("fail activation");
                }
            );

        if (cancel)
        {
            await activation.Should().ThrowAsync<OperationCanceledException>();
        }
        else
        {
            await activation.Should().ThrowAsync<InvalidOperationException>();
        }

        (await _store.GetActiveAsync(PricingSourceIds.OpenAi, ct))!.RawEvidence.Should().Be("old");
        (await _db.UsageEvents.AsNoTracking().SingleAsync(ct)).CostUsd.Should().Be(1m);
        (await _db.DailyAggregates.AsNoTracking().SingleAsync(ct)).CostUsd.Should().Be(1m);
    }

    private static UsageEvent Event(
        string key,
        CostBasis basis,
        decimal? cost,
        decimal? savings,
        string model = "gpt-test",
        Instant? occurredAt = null
    ) =>
        new()
        {
            Provider = Provider.OpenAI,
            OccurredAt = occurredAt ?? Instant.FromUtc(2026, 8, 25, 0, 0),
            IngestedAt = Instant.FromUtc(2026, 8, 25, 1, 0),
            ObservedAt = Instant.FromUtc(2026, 8, 25, 1, 0),
            Model = model,
            InputTokens = 1_000_000,
            CostUsd = cost,
            CacheSavingsUsd = savings,
            CostBasis = basis,
            SourceId = "repricing-test",
            SourceKind = SourceKind.ProviderApi,
            UsageScope = UsageScope.Api,
            EventKey = key,
            RawPayload = """{"processing":"standard","context":"short","region":"global"}""",
        };

    private static PricingSnapshotCandidate Candidate(
        string evidence,
        decimal input,
        params (LocalDate EffectiveFrom, decimal Input)[] later
    )
    {
        var entries = new[] { (new LocalDate(2026, 8, 1), input) }
            .Concat(later)
            .Select(entry => new OpenAiPriceEntry(
                "gpt-test",
                ["gpt-test"],
                entry.Item1,
                true,
                "standard",
                "short",
                "global",
                entry.Item2,
                0.5m,
                10m,
                1.25m
            ));
        var catalog = new OpenAiPriceCatalog(
            "USD",
            "https://openai.com/api/pricing/",
            Instant.FromUtc(2026, 8, 25, 0, 0),
            entries.ToArray()
        );
        return new PricingSnapshotCandidate(
            Provider.OpenAI,
            PricingSourceIds.OpenAi,
            Instant.FromUtc(2026, 8, 25, 0, 0),
            catalog.SourceUrl,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(evidence))),
            evidence,
            JsonSerializer.Serialize(catalog, JsonOptions)
        );
    }
}
