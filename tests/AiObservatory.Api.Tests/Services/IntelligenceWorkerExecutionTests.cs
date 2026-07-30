using AiObservatory.Api.Services;
using AiObservatory.Api.Services.Intelligence;
using AiObservatory.Data.Repositories;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace AiObservatory.Api.Tests.Services;

public class IntelligenceWorkerExecutionTests
{
    [Fact]
    public async Task AnalysisFailureDoesNotPreventTheBudgetArmOrStopTheWorker()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeClock(Instant.FromUtc(2026, 7, 30, 9, 0));
        var repository = Substitute.For<IUsageRepository>();
        repository
            .GetLatestInsightPeriodEndAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<LocalDate?>(new InvalidOperationException("failed")));
        var budgetCalled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var budget = Substitute.For<BudgetAlertService>(
            repository,
            clock,
            Substitute.For<IAlertNotifier>(),
            NullLogger<BudgetAlertService>.Instance
        );
        budget
            .CheckAndAlertAsync(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                budgetCalled.TrySetResult();
                return Task.CompletedTask;
            });
        var services = new ServiceCollection()
            .AddSingleton(repository)
            .AddSingleton(budget)
            .BuildServiceProvider();
        var logger = new CapturingLogger();
        var worker = new IntelligenceWorkerService(
            services.GetRequiredService<IServiceScopeFactory>(),
            clock,
            logger
        );

        await worker.StartAsync(ct);
        try
        {
            await budgetCalled.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);

            logger.Messages.Should().Contain(m => m.Contains("catchup failed"));
            worker.ExecuteTask.Should().NotBeNull();
            worker.ExecuteTask!.IsCompleted.Should().BeFalse();
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    private sealed class CapturingLogger : ILogger<IntelligenceWorkerService>
    {
        public List<string> Messages { get; } = [];

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => Messages.Add(formatter(state, exception));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose() { }
        }
    }
}
