using System.Collections.Concurrent;
using AiObservatory.Data;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Repositories;
using AiObservatory.Ingest.Sources;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using Npgsql;
using NSubstitute;
using Testcontainers.PostgreSql;

namespace AiObservatory.Ingest.Tests.Services;

[Collection("ProviderPollingWorker")]
public class ProviderPollingWorkerServiceTests(ProviderPollingDatabase database)
{
    [Fact]
    public async Task ReportsNoCompletedCycleBeforeItStarts()
    {
        await using var harness = CreateWorker();

        harness.Worker.CyclesCompleted.Should().Be(0);
        harness.Worker.LastCycleCompletedAt.Should().BeNull();
    }

    [Fact]
    public async Task RecordsTheCycleOnceAPollCompletes()
    {
        var current = Instant.FromUtc(2026, 7, 28, 9, 0);
        await using var harness = CreateWorker(current);

        await harness.Worker.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await WaitUntilAsync(() => harness.Worker.CyclesCompleted > 0);
            harness.Worker.LastCycleCompletedAt.Should().Be(current);
            harness.Worker.ExecuteTask.Should().NotBeNull();
            harness.Worker.ExecuteTask!.IsCompleted.Should().BeFalse();
        }
        finally
        {
            await harness.Worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ReportsACycleAtTheUnixEpoch()
    {
        var epoch = Instant.FromUnixTimeTicks(0);
        await using var harness = CreateWorker(epoch);

        await harness.Worker.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await WaitUntilAsync(() => harness.Worker.CyclesCompleted > 0);
            harness.Worker.LastCycleCompletedAt.Should().Be(epoch);
        }
        finally
        {
            await harness.Worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task RunPollAsync_PollsEveryRegisteredSourceAndPersistsLatestObservation()
    {
        var first = Source("first-source", new SourceIngestionResult(Instant.FromUtc(2026, 8, 23, 23, 0)));
        var second = Source("second-source", new SourceIngestionResult(null));
        await using var harness = CreateWorker(
            sources: [first, second],
            definitions: [Definition("first-source"), Definition("second-source")]
        );

        await harness.Worker.RunPollAsync(
            new LocalDate(2026, 8, 20),
            new LocalDate(2026, 8, 23),
            TestContext.Current.CancellationToken
        );

        await first
            .Received(1)
            .IngestAsync(new LocalDate(2026, 8, 20), new LocalDate(2026, 8, 23), Arg.Any<CancellationToken>());
        await second
            .Received(1)
            .IngestAsync(new LocalDate(2026, 8, 20), new LocalDate(2026, 8, 23), Arg.Any<CancellationToken>());
        var state = await harness.LoadStateAsync("first-source");
        state.LatestObservationAt.Should().Be(Instant.FromUtc(2026, 8, 23, 23, 0));
        state.LastSuccessAt.Should().Be(Instant.FromUtc(2026, 8, 24, 12, 0));
        state.ConsecutiveFailureCount.Should().Be(0);
    }

    [Fact]
    public async Task RunPollAsync_IsolatesFailuresAndPersistsOnlySanitizedErrorText()
    {
        var failed = Substitute.For<IUsageSource>();
        failed.SourceId.Returns("failed-source");
        failed
            .IngestAsync(Arg.Any<LocalDate>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns(
                Task.FromException<SourceIngestionResult>(
                    new InvalidOperationException(
                        "first line\r\nhttps://example.test/report?signature=secret-token next " + new string('x', 600)
                    )
                )
            );
        var continued = Source("continued-source", new SourceIngestionResult(null));
        await using var harness = CreateWorker(
            sources: [failed, continued],
            definitions: [Definition("failed-source"), Definition("continued-source")]
        );

        await harness.Worker.RunPollAsync(
            new LocalDate(2026, 8, 23),
            new LocalDate(2026, 8, 23),
            TestContext.Current.CancellationToken
        );

        await continued
            .Received(1)
            .IngestAsync(Arg.Any<LocalDate>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>());
        var state = await harness.LoadStateAsync("failed-source");
        state.ConsecutiveFailureCount.Should().Be(1);
        state
            .LastError.Should()
            .HaveLength(500)
            .And.NotContain("secret-token")
            .And.NotContain("\r")
            .And.NotContain("\n");
        state.IsAvailable.Should().BeNull();
    }

    [Fact]
    public async Task RunPollAsync_SuccessResetsPersistedFailureState()
    {
        var source = Substitute.For<IUsageSource>();
        source.SourceId.Returns("recovering-source");
        source
            .IngestAsync(Arg.Any<LocalDate>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns(
                Task.FromException<SourceIngestionResult>(new InvalidOperationException("failed")),
                Task.FromResult(new SourceIngestionResult(null))
            );
        await using var harness = CreateWorker(sources: [source], definitions: [Definition("recovering-source")]);

        await harness.Worker.RunPollAsync(
            new LocalDate(2026, 8, 23),
            new LocalDate(2026, 8, 23),
            TestContext.Current.CancellationToken
        );
        await harness.Worker.RunPollAsync(
            new LocalDate(2026, 8, 23),
            new LocalDate(2026, 8, 23),
            TestContext.Current.CancellationToken
        );

        var state = await harness.LoadStateAsync("recovering-source");
        state.ConsecutiveFailureCount.Should().Be(0);
        state.LastError.Should().BeNull();
        state.IsAvailable.Should().BeTrue();
    }

    [Fact]
    public async Task RunPollAsync_UnavailabilitySurvivesAnOrdinaryFailureUntilSuccess()
    {
        var source = Substitute.For<IUsageSource>();
        source.SourceId.Returns("unavailable-source");
        source
            .IngestAsync(Arg.Any<LocalDate>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns(
                Task.FromException<SourceIngestionResult>(new SourceUnavailableException("not supported")),
                Task.FromException<SourceIngestionResult>(new InvalidOperationException("failed"))
            );
        await using var harness = CreateWorker(sources: [source], definitions: [Definition("unavailable-source")]);

        await harness.Worker.RunPollAsync(
            new LocalDate(2026, 8, 23),
            new LocalDate(2026, 8, 23),
            TestContext.Current.CancellationToken
        );
        await harness.Worker.RunPollAsync(
            new LocalDate(2026, 8, 23),
            new LocalDate(2026, 8, 23),
            TestContext.Current.CancellationToken
        );

        var state = await harness.LoadStateAsync("unavailable-source");
        state.IsConfigured.Should().BeTrue();
        state.IsAvailable.Should().BeFalse();
        state.ConsecutiveFailureCount.Should().Be(2);
    }

    [Fact]
    public async Task RunPollAsync_MarksDefinitionWithoutImplementationUnconfigured()
    {
        await using var harness = CreateWorker(definitions: [Definition("missing-source", isConfigured: true)]);

        await harness.Worker.RunPollAsync(
            new LocalDate(2026, 8, 23),
            new LocalDate(2026, 8, 23),
            TestContext.Current.CancellationToken
        );

        var state = await harness.LoadStateAsync("missing-source");
        state.IsConfigured.Should().BeFalse();
        state.ConsecutiveFailureCount.Should().Be(0);
        state.LastAttemptAt.Should().BeNull();
    }

    [Fact]
    public async Task RunPollAsync_PropagatesCancellationAndDoesNotPollLaterSources()
    {
        using var cts = new CancellationTokenSource();
        var cancelled = Substitute.For<IUsageSource>();
        cancelled.SourceId.Returns("cancelled-source");
        cancelled
            .IngestAsync(Arg.Any<LocalDate>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                cts.Cancel();
                return Task.FromCanceled<SourceIngestionResult>(call.Arg<CancellationToken>());
            });
        var later = Source("later-source", new SourceIngestionResult(null));
        await using var harness = CreateWorker(
            sources: [cancelled, later],
            definitions: [Definition("cancelled-source"), Definition("later-source")]
        );

        var act = () => harness.Worker.RunPollAsync(new LocalDate(2026, 8, 23), new LocalDate(2026, 8, 23), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        await later
            .DidNotReceive()
            .IngestAsync(Arg.Any<LocalDate>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunPollAsync_EscalatesTheThirdConsecutiveFailure()
    {
        var source = Substitute.For<IUsageSource>();
        source.SourceId.Returns("escalating-source");
        source
            .IngestAsync(Arg.Any<LocalDate>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<SourceIngestionResult>(new InvalidOperationException("failed")));
        var logger = new CapturingLogger();
        await using var harness = CreateWorker(
            sources: [source],
            definitions: [Definition("escalating-source")],
            logger: logger
        );

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await harness.Worker.RunPollAsync(
                new LocalDate(2026, 8, 23),
                new LocalDate(2026, 8, 23),
                TestContext.Current.CancellationToken
            );
        }

        logger.Messages.Should().Contain(message => message.Contains("3 consecutive polls", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DoesNotStartAnotherCycleWhileASourceCallIsStillRunning()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<SourceIngestionResult>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var calls = new ConcurrentQueue<byte>();
        var source = Substitute.For<IUsageSource>();
        source.SourceId.Returns("blocking-source");
        source
            .IngestAsync(Arg.Any<LocalDate>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                calls.Enqueue(0);
                entered.TrySetResult();
                return release.Task;
            });
        await using var harness = CreateWorker(
            sources: [source],
            definitions: [Definition("blocking-source")],
            pollingIntervalMinutes: 0
        );

        await harness.Worker.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            calls.Should().ContainSingle();
            harness.Worker.CyclesCompleted.Should().Be(0);
        }
        finally
        {
            release.TrySetResult(new SourceIngestionResult(null));
            await harness.Worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task StartupLogNamesDefinitionsWithoutKnowingProviderTypes()
    {
        var logger = new CapturingLogger();
        var source = Source("registered-source", new SourceIngestionResult(null));
        await using var harness = CreateWorker(
            sources: [source],
            definitions: [Definition("registered-source"), Definition("missing-source")],
            logger: logger
        );

        await harness.Worker.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await WaitUntilAsync(() => harness.Worker.CyclesCompleted > 0);
            logger
                .Messages.Should()
                .Contain(message =>
                    message.Contains("registered-source: enabled", StringComparison.Ordinal)
                    && message.Contains("missing-source: NOT CONFIGURED", StringComparison.Ordinal)
                );
        }
        finally
        {
            await harness.Worker.StopAsync(CancellationToken.None);
        }
    }

    private static IUsageSource Source(string sourceId, SourceIngestionResult result)
    {
        var source = Substitute.For<IUsageSource>();
        source.SourceId.Returns(sourceId);
        source.IngestAsync(Arg.Any<LocalDate>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>()).Returns(result);
        return source;
    }

    private static SourceDefinition Definition(string sourceId, bool isConfigured = true) =>
        new(sourceId, isConfigured, Duration.FromHours(1));

    private WorkerHarness CreateWorker(
        Instant? current = null,
        IReadOnlyList<IUsageSource>? sources = null,
        IReadOnlyList<SourceDefinition>? definitions = null,
        CapturingLogger? logger = null,
        int pollingIntervalMinutes = 60
    )
    {
        var services = new ServiceCollection();
        services.AddDbContext<AiObservatoryDbContext>(options =>
            options.UseNpgsql(database.ConnectionString, npgsql => npgsql.UseNodaTime())
        );
        services.AddScoped<SourceSyncStateStore>();
        foreach (var source in sources ?? [])
        {
            services.AddScoped<IUsageSource>(_ => source);
        }
        foreach (var definition in definitions ?? [])
        {
            services.AddSingleton(definition);
        }
        var provider = services.BuildServiceProvider();
        var worker = new ProviderPollingWorkerService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new FakeClock(current ?? Instant.FromUtc(2026, 8, 24, 12, 0)),
            logger ?? new CapturingLogger(),
            Options.Create(new IngestOptions { LookbackDays = 1, PollingIntervalMinutes = pollingIntervalMinutes })
        );
        return new WorkerHarness(provider, worker);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class WorkerHarness(ServiceProvider services, ProviderPollingWorkerService worker) : IAsyncDisposable
    {
        public ProviderPollingWorkerService Worker { get; } = worker;

        public async Task<SourceSyncState> LoadStateAsync(string sourceId)
        {
            await using var scope = services.CreateAsyncScope();
            return await scope
                .ServiceProvider.GetRequiredService<AiObservatoryDbContext>()
                .SourceSyncStates.AsNoTracking()
                .SingleAsync(state => state.SourceId == sourceId, TestContext.Current.CancellationToken);
        }

        public ValueTask DisposeAsync() => services.DisposeAsync();
    }

    private sealed class CapturingLogger : ILogger<ProviderPollingWorkerService>
    {
        private readonly Lock _gate = new();
        private readonly List<string> _messages = [];

        public IReadOnlyList<string> Messages
        {
            get
            {
                lock (_gate)
                {
                    return [.. _messages];
                }
            }
        }

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            lock (_gate)
            {
                _messages.Add(formatter(state, exception));
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose() { }
        }
    }
}

[CollectionDefinition("ProviderPollingWorker", DisableParallelization = true)]
public sealed class ProviderPollingWorkerCollection : ICollectionFixture<ProviderPollingDatabase>;

public sealed class ProviderPollingDatabase : IAsyncLifetime
{
    private PostgreSqlContainer? _container;

    public string ConnectionString { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        var baseConnection = Environment.GetEnvironmentVariable("TEST_DB_CONNECTION");
        if (string.IsNullOrWhiteSpace(baseConnection))
        {
            _container = new PostgreSqlBuilder("postgres:17").WithDatabase("postgres").Build();
            await _container.StartAsync();
            baseConnection = _container.GetConnectionString();
        }
        ConnectionString = new NpgsqlConnectionStringBuilder(baseConnection)
        {
            Database = $"aiobs_test_worker_{Guid.NewGuid():N}",
        }.ConnectionString;
        var options = new DbContextOptionsBuilder<AiObservatoryDbContext>()
            .UseNpgsql(ConnectionString, npgsql => npgsql.UseNodaTime())
            .Options;
        await using var db = new AiObservatoryDbContext(options);
        await db.Database.MigrateAsync();
    }

    public async ValueTask DisposeAsync()
    {
        var options = new DbContextOptionsBuilder<AiObservatoryDbContext>()
            .UseNpgsql(ConnectionString, npgsql => npgsql.UseNodaTime())
            .Options;
        await using var db = new AiObservatoryDbContext(options);
        await db.Database.EnsureDeletedAsync();
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}
