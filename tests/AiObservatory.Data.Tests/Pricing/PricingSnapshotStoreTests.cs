using System.Data.Common;
using System.Text.Json;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Pricing;
using AiObservatory.Data.Pricing.Catalogs;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NodaTime;
using NodaTime.Serialization.SystemTextJson;
using Npgsql;

namespace AiObservatory.Data.Tests.Pricing;

[Trait("Category", "Integration")]
public sealed class PricingSnapshotStoreTests : IAsyncLifetime
{
    private static readonly Instant RetrievedAt = Instant.FromUtc(2026, 8, 24, 20, 0);
    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions(
        JsonSerializerDefaults.Web
    ).ConfigureForNodaTime(DateTimeZoneProviders.Tzdb);

    private string _connectionString = null!;
    private DbContextOptions<AiObservatoryDbContext> _options = null!;
    private AiObservatoryDbContext _db = null!;
    private PricingSnapshotStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        var baseConnection =
            Environment.GetEnvironmentVariable("TEST_DB_CONNECTION")
            ?? "Host=localhost;Database=aiobs_test;Username=postgres;Password=postgres";
        _connectionString = new NpgsqlConnectionStringBuilder(baseConnection)
        {
            Database = $"aiobs_test_pricing_{Guid.NewGuid():N}",
        }.ConnectionString;
        _options = new DbContextOptionsBuilder<AiObservatoryDbContext>()
            .UseNpgsql(_connectionString, options => options.UseNodaTime())
            .Options;
        _db = new AiObservatoryDbContext(_options);
        await _db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        _store = new PricingSnapshotStore(_db);
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
    public async Task ActivateRetainsImmutableEvidenceAndChangesOnlyTheActiveSnapshot()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = Candidate('a', 1m, "# exact first-party Markdown\n\n| model | price |\n");

        (await _store.ActivateAsync(first, ct)).Should().Be(PricingActivationResult.Activated);
        (
            await _store.ActivateAsync(
                first,
                ct,
                (_, _) => throw new InvalidOperationException("unchanged activation invoked its callback")
            )
        )
            .Should()
            .Be(PricingActivationResult.Unchanged);
        (await _store.ActivateAsync(Candidate('b', 2m, "second exact document"), ct))
            .Should()
            .Be(PricingActivationResult.Activated);

        var snapshots = await _db.PricingSnapshots.AsNoTracking().OrderBy(x => x.RetrievedAt).ToListAsync(ct);
        snapshots.Should().HaveCount(2);
        snapshots.Single(x => x.IsActive).ContentHash.Should().Be(new string('b', 64));
        snapshots
            .Single(x => !x.IsActive)
            .Should()
            .BeEquivalentTo(
                new
                {
                    ContentHash = new string('a', 64),
                    RawEvidence = "# exact first-party Markdown\n\n| model | price |\n",
                }
            );
        (await _store.GetActiveAsync(PricingSourceIds.OpenAi, ct))!.ContentHash.Should().Be(new string('b', 64));
    }

    [Fact]
    public async Task ActivateRollsBackSnapshotAndCallbackWritesWhenCallbackFails()
    {
        var ct = TestContext.Current.CancellationToken;
        var original = Candidate('a', 1m, "original");
        await _store.ActivateAsync(original, ct);

        var act = () =>
            _store.ActivateAsync(
                Candidate('b', 2m, "rejected replacement"),
                ct,
                async (_, callbackCt) =>
                {
                    _db.SourceSyncStates.Add(
                        new SourceSyncState
                        {
                            SourceId = "callback-side-effect",
                            ExpectedRefreshIntervalSeconds = 86_400,
                        }
                    );
                    await _db.SaveChangesAsync(callbackCt);
                    throw new InvalidOperationException("repricing failed");
                }
            );

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("repricing failed");
        _db.ChangeTracker.Clear();
        (await _db.PricingSnapshots.AsNoTracking().SingleAsync(ct)).ContentHash.Should().Be(original.ContentHash);
        (await _store.GetActiveAsync(PricingSourceIds.OpenAi, ct))!.ContentHash.Should().Be(original.ContentHash);
        (await _db.SourceSyncStates.AsNoTracking().CountAsync(ct)).Should().Be(0);
    }

    [Fact]
    public async Task ActivateValidatesTrustInputsBeforeChangingStoredState()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.ActivateAsync(Candidate('a', 1m, "original"), ct);
        var transactions = new TransactionCounter();
        var options = new DbContextOptionsBuilder<AiObservatoryDbContext>()
            .UseNpgsql(_connectionString, builder => builder.UseNodaTime())
            .AddInterceptors(transactions)
            .Options;
        await using var validationDb = new AiObservatoryDbContext(options);
        var invalid = Candidate('b', 2m, "invalid") with { SourceId = PricingSourceIds.Claude };

        var act = () => new PricingSnapshotStore(validationDb).ActivateAsync(invalid, ct);

        await act.Should().ThrowAsync<ArgumentException>();
        transactions.Started.Should().Be(0);
        _db.ChangeTracker.Clear();
        var saved = await _db.PricingSnapshots.AsNoTracking().SingleAsync(ct);
        saved.ContentHash.Should().Be(new string('a', 64));
        saved.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task ConcurrentActivationsLeaveOneActiveSnapshotPerSource()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var firstDb = new AiObservatoryDbContext(_options);
        await using var secondDb = new AiObservatoryDbContext(_options);

        var results = await Task.WhenAll(
            new PricingSnapshotStore(firstDb).ActivateAsync(Candidate('a', 1m, "first"), ct),
            new PricingSnapshotStore(secondDb).ActivateAsync(Candidate('b', 2m, "second"), ct)
        );

        results.Should().OnlyContain(result => result == PricingActivationResult.Activated);
        var snapshots = await _db.PricingSnapshots.AsNoTracking().ToListAsync(ct);
        snapshots.Should().HaveCount(2);
        snapshots.Count(x => x.IsActive).Should().Be(1);
    }

    [Fact]
    public async Task ConcurrentIdenticalActivationsReturnOneUnchangedWithoutDuplicatingEvidence()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var firstDb = new AiObservatoryDbContext(_options);
        await using var secondDb = new AiObservatoryDbContext(_options);
        var candidate = Candidate('a', 1m, "same evidence");

        var results = await Task.WhenAll(
            new PricingSnapshotStore(firstDb).ActivateAsync(candidate, ct),
            new PricingSnapshotStore(secondDb).ActivateAsync(candidate, ct)
        );

        results.Should().ContainSingle(result => result == PricingActivationResult.Activated);
        results.Should().ContainSingle(result => result == PricingActivationResult.Unchanged);
        (await _db.PricingSnapshots.AsNoTracking().CountAsync(ct)).Should().Be(1);
    }

    [Fact]
    public async Task PostgreSqlEnforcesSnapshotUniquenessAndColumnTypes()
    {
        var ct = TestContext.Current.CancellationToken;
        var normalized = ValidCatalogJson(1m);
        _db.PricingSnapshots.AddRange(Snapshot('a', false, normalized), Snapshot('a', false, normalized));

        var duplicateHash = () => _db.SaveChangesAsync(ct);

        await duplicateHash.Should().ThrowAsync<DbUpdateException>();
        _db.ChangeTracker.Clear();
        _db.PricingSnapshots.AddRange(Snapshot('a', true, normalized), Snapshot('b', true, normalized));

        var duplicateActive = () => _db.SaveChangesAsync(ct);

        await duplicateActive.Should().ThrowAsync<DbUpdateException>();
        _db.ChangeTracker.Clear();
        var columnTypes = await _db
            .Database.SqlQueryRaw<ColumnType>(
                """
                SELECT "column_name" AS "Name", "data_type" AS "Type"
                FROM information_schema.columns
                WHERE "table_name" = 'PricingSnapshots'
                  AND "column_name" IN ('RawEvidence', 'NormalizedCatalog')
                ORDER BY "column_name"
                """
            )
            .ToListAsync(ct);
        columnTypes
            .Should()
            .BeEquivalentTo([new ColumnType("NormalizedCatalog", "jsonb"), new ColumnType("RawEvidence", "text")]);
    }

    [Fact]
    public async Task GetCatalogForDateUsesTheActiveCatalogAndHonoursFutureEffectiveEntries()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.ActivateAsync(Candidate('a', 1m, "first"), ct);
        await _store.ActivateAsync(Candidate('b', 2m, "second", new LocalDate(2026, 9, 1)), ct);

        (await _store.GetCatalogForDateAsync(Provider.OpenAI, new LocalDate(2026, 7, 31), ct)).Should().BeNull();
        var august = await _store.GetCatalogForDateAsync(Provider.OpenAI, new LocalDate(2026, 8, 31), ct);
        var september = await _store.GetCatalogForDateAsync(Provider.OpenAI, new LocalDate(2026, 9, 1), ct);

        august.Should().NotBeNull();
        september.Should().NotBeNull();
        var catalog = JsonSerializer.Deserialize<OpenAiPriceCatalog>(september!.NormalizedCatalog, JsonOptions)!;
        catalog.Resolve("gpt-5.4", "standard", "short", "global", new LocalDate(2026, 8, 31))!.Input.Should().Be(1m);
        catalog.Resolve("gpt-5.4", "standard", "short", "global", new LocalDate(2026, 9, 1))!.Input.Should().Be(2m);
        august!.ContentHash.Should().Be(september.ContentHash);
    }

    [Fact]
    public void ProviderCatalogValidatorsRejectInvalidProviderSpecificShapes()
    {
        var date = new LocalDate(2026, 8, 24);
        var source = "https://example.com/pricing";
        var invalidOpenAi = new OpenAiPriceCatalog(
            "EUR",
            source,
            RetrievedAt,
            [new("gpt", ["gpt"], date, false, "", "short", "global", 1m, 0.1m, 2m)]
        );
        var invalidAnthropic = new AnthropicPriceCatalog(
            "USD",
            source,
            RetrievedAt,
            [new("claude", ["claude"], date, false, 1m, 2m, 0.1m, 1.25m, 2m, null, null, null, null, 0m)]
        );
        var invalidKimi = new KimiPriceCatalog(
            "USD",
            source,
            RetrievedAt,
            [new("kimi", ["kimi"], date, false, 0m, 1m, 2m, false, null)]
        );
        var invalidGoogle = new GooglePriceCatalog(
            "USD",
            source,
            RetrievedAt,
            [
                new(
                    "",
                    "sku",
                    ["product"],
                    date,
                    false,
                    "us",
                    "text",
                    "standard",
                    "none",
                    128_000,
                    "1M tokens",
                    "ACCOUNT",
                    1m
                ),
            ]
        );

        ((Action)invalidOpenAi.Validate).Should().Throw<InvalidDataException>();
        ((Action)invalidAnthropic.Validate).Should().Throw<InvalidDataException>();
        ((Action)invalidKimi.Validate).Should().Throw<InvalidDataException>();
        ((Action)invalidGoogle.Validate).Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void ProviderCatalogValidatorsAcceptCompleteExplicitShapes()
    {
        var date = new LocalDate(2026, 8, 24);
        var source = "https://example.com/pricing";
        var catalogs = new Action[]
        {
            new OpenAiPriceCatalog("USD", source, RetrievedAt, [OpenAiEntry(date, 1m)]).Validate,
            new AnthropicPriceCatalog(
                "USD",
                source,
                RetrievedAt,
                [new("claude", ["claude"], date, false, 1m, 2m, 0.1m, 1.25m, 2m, 0.5m, 1m, 2m, 4m, 1.1m)]
            ).Validate,
            new KimiPriceCatalog(
                "USD",
                source,
                RetrievedAt,
                [new("kimi", ["kimi"], date, false, 0.1m, 1m, 2m, false, 0.6m)]
            ).Validate,
            new GooglePriceCatalog(
                "USD",
                source,
                RetrievedAt,
                [
                    new(
                        "Gemini",
                        "sku",
                        ["gemini"],
                        date,
                        true,
                        "us",
                        "text",
                        "standard",
                        "none",
                        128_000,
                        "1M tokens",
                        "ACCOUNT",
                        1m
                    ),
                ]
            ).Validate,
        };

        catalogs.Should().AllSatisfy(validate => validate.Should().NotThrow());
    }

    [Fact]
    public void ProviderCatalogValidatorRejectsDuplicateOrUnorderedEffectiveWindows()
    {
        var first = OpenAiEntry(new LocalDate(2026, 9, 1), 2m);
        var earlier = OpenAiEntry(new LocalDate(2026, 8, 24), 1m);
        var duplicate = OpenAiEntry(new LocalDate(2026, 9, 1), 3m);

        ((Action)new OpenAiPriceCatalog("USD", "https://example.com", RetrievedAt, [first, earlier]).Validate)
            .Should()
            .Throw<InvalidDataException>();
        ((Action)new OpenAiPriceCatalog("USD", "https://example.com", RetrievedAt, [first, duplicate]).Validate)
            .Should()
            .Throw<InvalidDataException>();
    }

    private static PricingSnapshotCandidate Candidate(char hash, decimal input, string raw, LocalDate? future = null) =>
        new(
            Provider.OpenAI,
            PricingSourceIds.OpenAi,
            RetrievedAt.Plus(Duration.FromMinutes(hash - 'a')),
            "https://developers.openai.com/api/docs/pricing.md",
            new string(hash, 64),
            raw,
            ValidCatalogJson(input, future)
        );

    private static string ValidCatalogJson(decimal input, LocalDate? future = null)
    {
        var entries = new List<OpenAiPriceEntry> { OpenAiEntry(new LocalDate(2026, 8, 24), 1m) };
        if (future is not null)
        {
            entries.Add(OpenAiEntry(future.Value, input));
        }
        else
        {
            entries[0] = OpenAiEntry(new LocalDate(2026, 8, 24), input);
        }

        return JsonSerializer.Serialize(
            new OpenAiPriceCatalog("USD", "https://developers.openai.com/api/docs/pricing.md", RetrievedAt, entries),
            JsonOptions
        );
    }

    private static OpenAiPriceEntry OpenAiEntry(LocalDate effectiveFrom, decimal input) =>
        new("gpt-5.4", ["gpt-5.4"], effectiveFrom, false, "standard", "short", "global", input, 0.25m, 10m);

    private static PricingSnapshot Snapshot(char hash, bool active, string normalized) =>
        new()
        {
            Provider = Provider.OpenAI,
            SourceId = PricingSourceIds.OpenAi,
            RetrievedAt = RetrievedAt,
            SourceUrl = "https://developers.openai.com/api/docs/pricing.md",
            ContentHash = new string(hash, 64),
            RawEvidence = "raw Markdown",
            NormalizedCatalog = normalized,
            IsActive = active,
        };

    private sealed record ColumnType(string Name, string Type);

    private sealed class TransactionCounter : DbTransactionInterceptor
    {
        public int Started { get; private set; }

        public override ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
            DbConnection connection,
            TransactionStartingEventData eventData,
            InterceptionResult<DbTransaction> result,
            CancellationToken cancellationToken = default
        )
        {
            Started++;
            return ValueTask.FromResult(result);
        }
    }
}
