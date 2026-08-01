using System.Collections.Concurrent;
using AiObservatory.Data.Repositories;
using AiObservatory.Ingest.Services.Anthropic;
using AiObservatory.Ingest.Services.GitHub;
using AiObservatory.Ingest.Services.OpenAi;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace AiObservatory.Ingest.Tests.Services;

/// <summary>
/// The worker's cycle counters are what <c>/healthz</c> reports, and that endpoint is wired
/// to App Service's <c>healthCheckPath</c> — so a counter that never moves would put the
/// deployment straight back into the failure this worker just came out of: running,
/// reported healthy, doing nothing.
/// </summary>
public class ProviderPollingWorkerServiceTests
{
    /// <summary>
    /// No provider services registered, so every <c>TryIngestAsync</c> resolves null and
    /// returns immediately. That is a real supported configuration (an unconfigured worker
    /// is a documented no-op) and it lets a cycle complete without any network at all.
    /// </summary>
    private static ProviderPollingWorkerService CreateWorker(
        Instant? now = null,
        Action<IServiceCollection>? configureServices = null,
        CapturingLogger? logger = null,
        int pollingIntervalMinutes = 60,
        string[]? githubRepoAllowlist = null
    )
    {
        var services = new ServiceCollection();
        configureServices?.Invoke(services);
        var provider = services.BuildServiceProvider();
        return new ProviderPollingWorkerService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new FakeClock(now ?? Instant.FromUtc(2026, 7, 28, 9, 0)),
            logger ?? new CapturingLogger(),
            // A long interval so the worker completes exactly one cycle then parks on the
            // delay, rather than spinning while the assertions run.
            Options.Create(
                new IngestOptions
                {
                    PollingIntervalMinutes = pollingIntervalMinutes,
                    LookbackDays = 1,
                    GitHubRepoAllowlist = githubRepoAllowlist ?? [],
                }
            )
        );
    }

    private static void AddAnthropic(IServiceCollection services, IAnthropicUsageClient client) =>
        services.AddSingleton(
            new AnthropicIngestionService(
                client,
                Substitute.For<IUsageRepository>(),
                new FakeClock(Instant.FromUtc(2026, 7, 28, 9, 0)),
                NullLogger<AnthropicIngestionService>.Instance
            )
        );

    private static void AddOpenAi(IServiceCollection services, IOpenAiUsageClient client) =>
        services.AddSingleton(
            new OpenAiIngestionService(
                client,
                Substitute.For<IUsageRepository>(),
                new FakeClock(Instant.FromUtc(2026, 7, 28, 9, 0)),
                NullLogger<OpenAiIngestionService>.Instance
            )
        );

    private static void AddGitHub(
        IServiceCollection services,
        string[] repoAllowlist,
        IGitHubActivityRepository repository
    ) =>
        services.AddSingleton(
            new GitHubIngestionService(
                Substitute.For<IGitHubActivityClient>(),
                repository,
                Options.Create(new IngestOptions { GitHubRepoAllowlist = repoAllowlist }),
                NullLogger<GitHubIngestionService>.Instance,
                new FakeClock(Instant.FromUtc(2026, 7, 28, 9, 0))
            )
        );

    /// <summary>
    /// Polls for a real completion signal rather than sleeping a guessed duration: a fixed
    /// delay plus an immediate assert is green locally and flaky on a contended runner.
    /// The timeout is generous because it only bounds failure, never success.
    /// </summary>
    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));

        while (!condition())
        {
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, timeout.Token);
        }
    }

    [Fact]
    public async Task ReportsNoCompletedCycleBeforeItStarts()
    {
        var worker = CreateWorker();

        worker.CyclesCompleted.Should().Be(0);
        worker.LastCycleCompletedAt.Should().BeNull("/healthz must not claim a cycle has run before one has");

        await Task.CompletedTask;
    }

    [Fact]
    public async Task RecordsTheCycleOnceAPollCompletes()
    {
        var ct = TestContext.Current.CancellationToken;
        var worker = CreateWorker();

        await worker.StartAsync(ct);
        try
        {
            await WaitUntilAsync(() => worker.CyclesCompleted > 0, ct);

            worker.CyclesCompleted.Should().BeGreaterThan(0);
            worker
                .LastCycleCompletedAt.Should()
                .Be(
                    Instant.FromUtc(2026, 7, 28, 9, 0),
                    "the timestamp comes from the injected clock, not the wall clock"
                );
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Zero ticks is a valid Instant, not an "unset" marker. Completion is therefore keyed
    /// on the cycle counter — keying it on the timestamp would make a worker whose clock
    /// reads the epoch run cycles while /healthz insisted none had happened.
    /// </summary>
    [Fact]
    public async Task ReportsACycleEvenWhenItCompletesAtTheUnixEpoch()
    {
        var ct = TestContext.Current.CancellationToken;
        var epoch = Instant.FromUnixTimeTicks(0);
        var worker = CreateWorker(epoch);

        await worker.StartAsync(ct);
        try
        {
            await WaitUntilAsync(() => worker.CyclesCompleted > 0, ct);

            worker
                .LastCycleCompletedAt.Should()
                .Be(epoch, "0 ticks is 1970-01-01T00:00:00Z, a real timestamp — not a null sentinel");
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task KeepsThePollLoopRunningBetweenCycles()
    {
        var ct = TestContext.Current.CancellationToken;
        var worker = CreateWorker();

        await worker.StartAsync(ct);
        try
        {
            await WaitUntilAsync(() => worker.CyclesCompleted > 0, ct);

            // The condition /healthz actually keys on. A completed ExecuteTask while the
            // host is still up is the silent death the endpoint exists to surface, so it
            // must NOT be true merely because a cycle finished and the loop is waiting.
            worker.ExecuteTask.Should().NotBeNull();
            worker
                .ExecuteTask!.IsCompleted.Should()
                .BeFalse("the worker parks on the polling delay between cycles — it has not stopped");
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task AFailedProviderDoesNotStopTheRemainingProvidersOrTheWorker()
    {
        var ct = TestContext.Current.CancellationToken;
        var anthropic = Substitute.For<IAnthropicUsageClient>();
        anthropic
            .GetUsageAsync(Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<AnthropicUsageRecord>>(new("failed")));
        var openAiCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var openAi = Substitute.For<IOpenAiUsageClient>();
        openAi
            .GetDailyUsageAsync(Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                openAiCalled.TrySetResult();
                return Task.FromResult<IReadOnlyList<OpenAiUsageRecord>>([]);
            });
        var logger = new CapturingLogger();
        var worker = CreateWorker(
            configureServices: services =>
            {
                AddAnthropic(services, anthropic);
                AddOpenAi(services, openAi);
            },
            logger: logger
        );

        await worker.StartAsync(ct);
        try
        {
            await openAiCalled.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);

            logger.Messages.Should().Contain(m => m.Contains("Anthropic ingestion failed"));
            worker.ExecuteTask.Should().NotBeNull();
            worker.ExecuteTask!.IsCompleted.Should().BeFalse();
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task EscalatesAProviderAfterThreeConsecutiveFailures()
    {
        var ct = TestContext.Current.CancellationToken;
        var anthropic = Substitute.For<IAnthropicUsageClient>();
        anthropic
            .GetUsageAsync(Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<AnthropicUsageRecord>>(new("failed")));
        var logger = new CapturingLogger();
        var worker = CreateWorker(
            configureServices: services => AddAnthropic(services, anthropic),
            logger: logger,
            pollingIntervalMinutes: 0
        );

        await worker.StartAsync(ct);
        try
        {
            await WaitUntilAsync(() => logger.Messages.Any(m => m.Contains("3 consecutive polls")), ct);

            logger.Messages.Should().Contain(m => m.Contains("provider may be misconfigured"));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task NamesEveryProviderArmAtStartup()
    {
        var ct = TestContext.Current.CancellationToken;
        var logger = new CapturingLogger();
        var worker = CreateWorker(logger: logger);

        await worker.StartAsync(ct);
        try
        {
            await WaitUntilAsync(() => worker.CyclesCompleted > 0, ct);

            logger
                .Messages.Should()
                .Contain(m =>
                    m.Contains("Anthropic: NOT CONFIGURED")
                    && m.Contains("Copilot: NOT CONFIGURED")
                    && m.Contains("Google: NOT CONFIGURED")
                    && m.Contains("OpenAI: NOT CONFIGURED")
                    && m.Contains("GitHub: NOT CONFIGURED")
                );
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task DoesNotStartAnotherCycleWhileAProviderCallIsStillRunning()
    {
        var ct = TestContext.Current.CancellationToken;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<IReadOnlyList<AnthropicUsageRecord>>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var calls = new ConcurrentQueue<byte>();
        var anthropic = Substitute.For<IAnthropicUsageClient>();
        anthropic
            .GetUsageAsync(Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                calls.Enqueue(0);
                entered.TrySetResult();
                return release.Task;
            });
        var worker = CreateWorker(
            configureServices: services => AddAnthropic(services, anthropic),
            pollingIntervalMinutes: 0
        );

        await worker.StartAsync(ct);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);

            calls.Should().ContainSingle();
            worker.CyclesCompleted.Should().Be(0);
        }
        finally
        {
            release.TrySetResult([]);
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task EscalatesGitHubWhenEveryConfiguredRepositoryFails()
    {
        var ct = TestContext.Current.CancellationToken;
        string[] repoAllowlist = ["fixportal/one", "fixportal/two"];
        var repository = Substitute.For<IGitHubActivityRepository>();
        repository
            .GetBackfillStatusAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<GitHubBackfillStatus>(new InvalidOperationException("failed")));
        var logger = new CapturingLogger();
        var worker = CreateWorker(
            configureServices: services => AddGitHub(services, repoAllowlist, repository),
            logger: logger,
            githubRepoAllowlist: repoAllowlist
        );

        await worker.StartAsync(ct);
        try
        {
            await WaitUntilAsync(() => logger.Messages.Any(m => m.Contains("GitHub ingestion failed")), ct);

            worker.ExecuteTask.Should().NotBeNull();
            worker.ExecuteTask!.IsCompleted.Should().BeFalse();
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
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
