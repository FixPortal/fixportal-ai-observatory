using AiObservatory.Ingest;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;

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
    private static ProviderPollingWorkerService CreateWorker(Instant? now = null)
    {
        var services = new ServiceCollection().BuildServiceProvider();
        return new ProviderPollingWorkerService(
            services.GetRequiredService<IServiceScopeFactory>(),
            new FakeClock(now ?? Instant.FromUtc(2026, 7, 28, 9, 0)),
            NullLogger<ProviderPollingWorkerService>.Instance,
            // A long interval so the worker completes exactly one cycle then parks on the
            // delay, rather than spinning while the assertions run.
            Options.Create(new IngestOptions { PollingIntervalMinutes = 60, LookbackDays = 1 }));
    }

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
        worker.LastCycleCompletedAt.Should().BeNull(
            "/healthz must not claim a cycle has run before one has");

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
            worker.LastCycleCompletedAt.Should().Be(Instant.FromUtc(2026, 7, 28, 9, 0),
                "the timestamp comes from the injected clock, not the wall clock");
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

            worker.LastCycleCompletedAt.Should().Be(epoch,
                "0 ticks is 1970-01-01T00:00:00Z, a real timestamp — not a null sentinel");
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
            worker.ExecuteTask!.IsCompleted.Should().BeFalse(
                "the worker parks on the polling delay between cycles — it has not stopped");
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }
}
