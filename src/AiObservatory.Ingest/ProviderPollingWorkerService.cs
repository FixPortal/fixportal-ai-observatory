using AiObservatory.Ingest.Services.Anthropic;
using AiObservatory.Ingest.Services.Copilot;
using AiObservatory.Ingest.Services.Google;
using AiObservatory.Ingest.Services.OpenAi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;

namespace AiObservatory.Ingest;

// Polls each configured provider once per interval (default: hourly) and ingests
// the previous day's usage into the observatory database.
// A provider is skipped unless its required config key is present (see Program.cs).
public class ProviderPollingWorkerService(
    IServiceScopeFactory scopeFactory,
    IClock clock,
    ILogger<ProviderPollingWorkerService> logger,
    IOptions<IngestOptions> options) : BackgroundService
{
    // After this many consecutive failed polls a provider's log is escalated so persistent
    // breakage (misconfig, expired credential) stops reading as ordinary transient noise.
    private const int ConsecutiveFailureAlertThreshold = 3;

    // Per-provider consecutive-failure count. The worker runs a single poll loop with no
    // concurrency, so a plain dictionary is safe.
    private readonly Dictionary<string, int> _consecutiveFailures = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = options.Value.PollingInterval;
        var lookbackDays = Math.Max(1, options.Value.LookbackDays);
        logger.LogInformation(
            "Provider polling worker started (interval: {Interval}, lookback: {LookbackDays}d)", interval, lookbackDays);
        while (!stoppingToken.IsCancellationRequested)
        {
            var yesterday = clock.GetCurrentInstant().InUtc().Date.PlusDays(-1);
            // Trailing window ending yesterday, oldest first.
            var dates = Enumerable.Range(0, lookbackDays)
                .Select(offset => yesterday.PlusDays(-(lookbackDays - 1 - offset)))
                .ToList();
            await RunPollAsync(dates, stoppingToken);
            await Task.Delay(interval, stoppingToken);
        }
    }

    private async Task RunPollAsync(IReadOnlyList<LocalDate> dates, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var sp = scope.ServiceProvider;

        await TryIngestAsync<AnthropicIngestionService>(sp, "Anthropic",
            (s, d) => s.IngestAsync(d, ct), dates);
        await TryIngestAsync<CopilotIngestionService>(sp, "Copilot",
            (s, d) => s.IngestAsync(d, ct), dates);
        await TryIngestAsync<GoogleIngestionService>(sp, "Google",
            (s, d) => s.IngestAsync(d, ct), dates);
        await TryIngestAsync<OpenAiIngestionService>(sp, "OpenAI",
            (s, d) => s.IngestAsync(d, ct), dates);
    }

    private async Task TryIngestAsync<TService>(
        IServiceProvider sp, string name,
        Func<TService, LocalDate, Task> action, IReadOnlyList<LocalDate> dates)
        where TService : class
    {
        var service = sp.GetService<TService>();
        if (service is null)
        {
            return;
        }
        try
        {
            foreach (var date in dates)
            {
                await action(service, date);
            }
            _consecutiveFailures[name] = 0;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var count = _consecutiveFailures.GetValueOrDefault(name) + 1;
            _consecutiveFailures[name] = count;
            if (count >= ConsecutiveFailureAlertThreshold)
            {
                logger.LogError(ex,
                    "{Provider} ingestion has failed {Count} consecutive polls — provider may be misconfigured or unavailable",
                    name, count);
            }
            else
            {
                logger.LogError(ex, "{Provider} ingestion failed", name);
            }
        }
    }
}
