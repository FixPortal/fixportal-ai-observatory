using AiObservatory.Api.Services;
using AiObservatory.Data.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace AiObservatory.Api.Services.Intelligence;

public class IntelligenceWorkerService(
    IServiceScopeFactory scopeFactory,
    IClock clock,
    ILogger<IntelligenceWorkerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunAnalysisCatchupAsync(stoppingToken);

            var now = clock.GetCurrentInstant();
            var nextRun = now.InUtc().Date.PlusDays(1).AtMidnight().InUtc().ToInstant();
            var delay = (nextRun - now).ToTimeSpan();
            if (delay > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(delay, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    private async Task RunAnalysisCatchupAsync(CancellationToken stoppingToken)
    {
        try
        {
            var today = clock.GetCurrentInstant().InUtc().Date;
            var yesterday = today.PlusDays(-1);

            LocalDate? latestPeriodEnd;
            using (var scope = scopeFactory.CreateScope())
            {
                var repository = scope.ServiceProvider.GetRequiredService<IUsageRepository>();
                latestPeriodEnd = await repository.GetLatestInsightPeriodEndAsync(stoppingToken);
            }

            var start = latestPeriodEnd.HasValue ? latestPeriodEnd.Value.PlusDays(1) : yesterday;
            
            // Limit catch-up to a maximum of 7 days to prevent flooding on startup
            if (start < yesterday.PlusDays(-7))
            {
                start = yesterday.PlusDays(-7);
            }

            for (var date = start; date <= yesterday; date = date.PlusDays(1))
            {
                logger.LogInformation("Intelligence worker running analysis catchup for {Date}", date);
                await RunAnalysisAsync(date, stoppingToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Intelligence worker catchup failed");
        }
    }

    private async Task RunAnalysisAsync(LocalDate analysisDate, CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var generator = scope.ServiceProvider.GetRequiredService<IInsightGenerator>();
            var count = await generator.GenerateForDateAsync(analysisDate, ct);
            logger.LogInformation("Intelligence worker wrote {Count} insights for {Period}", count, analysisDate);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Graceful shutdown - propagate so the host stops the worker cleanly.
            throw;
        }
        catch (Exception ex)
        {
            // Broad by design: isolate a single daily run's failure so the
            // long-running worker survives to the next iteration.
            logger.LogError(ex, "Intelligence worker failed for date {Date}", analysisDate);
        }
    }
}
