using System.Security.Cryptography;
using System.Text;
using AiObservatory.Data;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Pricing;
using AiObservatory.Data.Pricing.Catalogs;
using AiObservatory.Ingest.Pricing;
using AiObservatory.Ingest.Sources;
using AiObservatory.Ingest.Tests.Services;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace AiObservatory.Ingest.Tests.Pricing;

[Collection("ProviderPollingWorker")]
public sealed class BundledPricingCatalogLoaderTests(ProviderPollingDatabase database)
{
    [Fact]
    public async Task ActivatesAllFourStrictlyValidatedBundlesOnColdStart()
    {
        await using var harness = await CreateHarnessAsync();

        await harness.Loader.LoadAsync(TestContext.Current.CancellationToken);

        var snapshots = await harness
            .Db.PricingSnapshots.AsNoTracking()
            .ToListAsync(TestContext.Current.CancellationToken);
        snapshots
            .Select(snapshot => snapshot.SourceId)
            .Should()
            .BeEquivalentTo(
                PricingSourceIds.OpenAi,
                PricingSourceIds.Claude,
                PricingSourceIds.Kimi,
                PricingSourceIds.GoogleCloudCatalog
            );
        snapshots.Should().OnlyContain(snapshot => snapshot.IsActive);
        var google = snapshots.Single(snapshot => snapshot.SourceId == PricingSourceIds.GoogleCloudCatalog);
        PricingCatalogJson.Deserialize<GooglePriceCatalog>(google.NormalizedCatalog).Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task HashesAndRetainsTheExactBundledJsonAsRawEvidence()
    {
        await using var harness = await CreateHarnessAsync();
        var raw = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Pricing", "Bundled", "openai.json"),
            TestContext.Current.CancellationToken
        );

        await harness.Loader.LoadAsync(TestContext.Current.CancellationToken);

        var snapshot = await harness
            .Db.PricingSnapshots.AsNoTracking()
            .SingleAsync(
                candidate => candidate.SourceId == PricingSourceIds.OpenAi,
                TestContext.Current.CancellationToken
            );
        snapshot.RawEvidence.Should().Be(raw);
        snapshot.ContentHash.Should().Be(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw))));
    }

    [Fact]
    public async Task DoesNotReplaceAnExistingActiveSnapshotWithTheBundle()
    {
        await using var harness = await CreateHarnessAsync();
        var raw = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Pricing", "Bundled", "openai.json"),
            TestContext.Current.CancellationToken
        );
        var catalog = PricingCatalogJson.Deserialize<OpenAiPriceCatalog>(raw);
        var current = PricingCandidate.Create(
            Provider.OpenAI,
            PricingSourceIds.OpenAi,
            catalog.RetrievedAt,
            catalog.SourceUrl,
            "newer remote evidence",
            catalog
        );
        await harness.Store.ActivateAsync(current, TestContext.Current.CancellationToken);

        await harness.Loader.LoadAsync(TestContext.Current.CancellationToken);

        var active = await harness.Store.GetActiveAsync(PricingSourceIds.OpenAi, TestContext.Current.CancellationToken);
        active!.ContentHash.Should().Be(current.ContentHash);
        (
            await harness.Db.PricingSnapshots.CountAsync(
                candidate => candidate.SourceId == PricingSourceIds.OpenAi,
                TestContext.Current.CancellationToken
            )
        )
            .Should()
            .Be(1);
    }

    [Fact]
    public async Task RejectsAnUnmappedBundleMemberWithoutAbortingTheBundlePass()
    {
        await using var harness = await CreateHarnessAsync();
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var bundleDirectory = Directory.CreateDirectory(Path.Combine(directory.FullName, "Pricing", "Bundled"));
            await File.WriteAllTextAsync(
                Path.Combine(bundleDirectory.FullName, "openai.json"),
                """
                {
                  "currency": "USD",
                  "sourceUrl": "https://developers.openai.com/api/docs/pricing.md",
                  "retrievedAt": "2026-08-24T12:00:00Z",
                  "entries": [],
                  "unexpected": true
                }
                """,
                TestContext.Current.CancellationToken
            );
            var loader = new BundledPricingCatalogLoader(
                harness.Store,
                NullLogger<BundledPricingCatalogLoader>.Instance,
                directory.FullName
            );

            await loader.LoadAsync(TestContext.Current.CancellationToken);

            (await harness.Store.GetActiveAsync(PricingSourceIds.OpenAi, TestContext.Current.CancellationToken))
                .Should()
                .BeNull();
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task BundleWaitsForRemoteActivationAndCannotOverwriteIt()
    {
        var services = new ServiceCollection();
        services.AddDbContext<AiObservatoryDbContext>(options =>
            options.UseNpgsql(database.ConnectionString, npgsql => npgsql.UseNodaTime())
        );
        await using var provider = services.BuildServiceProvider();
        await using var remoteScope = provider.CreateAsyncScope();
        await using var bundleScope = provider.CreateAsyncScope();
        var remoteDb = remoteScope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
        await remoteDb.PricingSnapshots.ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        await remoteDb.SourceSyncStates.ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        var remoteStore = new PricingSnapshotStore(remoteDb);
        var bundleStore = new PricingSnapshotStore(
            bundleScope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>()
        );
        var loader = new BundledPricingCatalogLoader(
            bundleStore,
            NullLogger<BundledPricingCatalogLoader>.Instance,
            AppContext.BaseDirectory
        );
        var raw = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Pricing", "Bundled", "openai.json"),
            TestContext.Current.CancellationToken
        );
        var catalog = PricingCatalogJson.Deserialize<OpenAiPriceCatalog>(raw);
        var remoteCandidate = PricingCandidate.Create(
            Provider.OpenAI,
            PricingSourceIds.OpenAi,
            catalog.RetrievedAt,
            catalog.SourceUrl,
            "remote evidence",
            catalog
        );
        var remoteHasLock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRemote = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var remoteActivation = remoteStore.ActivateAsync(
            remoteCandidate,
            TestContext.Current.CancellationToken,
            async (_, cancellationToken) =>
            {
                remoteHasLock.SetResult();
                await releaseRemote.Task.WaitAsync(cancellationToken);
            }
        );
        await remoteHasLock.Task.WaitAsync(TestContext.Current.CancellationToken);

        var bundleActivation = loader.LoadAsync(TestContext.Current.CancellationToken);
        try
        {
            await WaitForAdvisoryLockWaitAsync();
        }
        finally
        {
            releaseRemote.TrySetResult();
        }
        await Task.WhenAll(remoteActivation, bundleActivation);

        var active = await remoteStore.GetActiveAsync(PricingSourceIds.OpenAi, TestContext.Current.CancellationToken);
        active!.ContentHash.Should().Be(remoteCandidate.ContentHash);
        active.RawEvidence.Should().Be("remote evidence");
    }

    private async Task WaitForAdvisoryLockWaitAsync()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync(timeout.Token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1
                FROM pg_locks
                WHERE locktype = 'advisory'
                  AND NOT granted
                  AND database = (SELECT oid FROM pg_database WHERE datname = current_database())
            )
            """;
        while (!(bool)(await command.ExecuteScalarAsync(timeout.Token) ?? false))
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private async Task<LoaderHarness> CreateHarnessAsync()
    {
        var services = new ServiceCollection();
        services.AddDbContext<AiObservatoryDbContext>(options =>
            options.UseNpgsql(database.ConnectionString, npgsql => npgsql.UseNodaTime())
        );
        var provider = services.BuildServiceProvider();
        var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
        await db.PricingSnapshots.ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        await db.SourceSyncStates.ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        var store = new PricingSnapshotStore(db);
        return new LoaderHarness(
            provider,
            scope,
            db,
            store,
            new BundledPricingCatalogLoader(store, NullLogger<BundledPricingCatalogLoader>.Instance)
        );
    }

    private sealed class LoaderHarness(
        ServiceProvider services,
        AsyncServiceScope scope,
        AiObservatoryDbContext db,
        PricingSnapshotStore store,
        BundledPricingCatalogLoader loader
    ) : IAsyncDisposable
    {
        public AiObservatoryDbContext Db { get; } = db;
        public PricingSnapshotStore Store { get; } = store;
        public BundledPricingCatalogLoader Loader { get; } = loader;

        public async ValueTask DisposeAsync()
        {
            await scope.DisposeAsync();
            await services.DisposeAsync();
        }
    }
}
