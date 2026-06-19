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
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = options.Value.PollingInterval;
        logger.LogInformation("Provider polling worker started (interval: {Interval})", interval);
        while (!stoppingToken.IsCancellationRequested)
        {
            var yesterday = clock.GetCurrentInstant().InUtc().Date.PlusDays(-1);
            await RunPollAsync(yesterday, stoppingToken);
            await Task.Delay(interval, stoppingToken);
        }
    }

    private async Task RunPollAsync(LocalDate date, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var sp = scope.ServiceProvider;

        await TryIngestAsync<AnthropicIngestionService>(sp, "Anthropic",
            s => s.IngestAsync(date, ct), ct);
        await TryIngestAsync<CopilotIngestionService>(sp, "Copilot",
            s => s.IngestAsync(date, ct), ct);
        await TryIngestAsync<GoogleIngestionService>(sp, "Google",
            s => s.IngestAsync(date, ct), ct);
        await TryIngestAsync<OpenAiIngestionService>(sp, "OpenAI",
            s => s.IngestAsync(date, ct), ct);
    }

    private async Task TryIngestAsync<TService>(
        IServiceProvider sp, string name,
        Func<TService, Task> action, CancellationToken ct)
        where TService : class
    {
        var service = sp.GetService<TService>();
        if (service is null)
        {
            return;
        }
        try
        {
            await action(service);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex, "{Provider} ingestion failed", name);
        }
    }
}
