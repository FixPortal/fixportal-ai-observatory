using AiObservatory.Data.Pricing;
using AiObservatory.Data.Repositories;
using AiObservatory.Ingest.Sources;
using NodaTime;

namespace AiObservatory.Ingest.Pricing;

public sealed class PricingRefreshWorkerService(
    IServiceScopeFactory scopeFactory,
    IClock clock,
    ILogger<PricingRefreshWorkerService> logger
) : BackgroundService
{
    private static readonly Duration RefreshInterval = Duration.FromDays(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunOnceAsync(stoppingToken);
            await Task.Delay(TimeSpan.FromDays(1), stoppingToken);
        }
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;
        await services.GetRequiredService<BundledPricingCatalogLoader>().LoadAsync(cancellationToken);

        var sources = services.GetServices<IPricingSource>().ToDictionary(source => source.SourceId);
        var definitions = services.GetServices<PricingSourceDefinition>().ToList();
        var states = services.GetRequiredService<SourceSyncStateStore>();
        var store = services.GetRequiredService<PricingSnapshotStore>();
        var now = clock.GetCurrentInstant();

        foreach (var definition in definitions)
        {
            if (!definition.IsConfigured || !sources.TryGetValue(definition.SourceId, out var source))
            {
                await MarkUnconfiguredAsync(definition, states, now, cancellationToken);
                continue;
            }

            var state = await states.GetAsync(source.SourceId, cancellationToken);
            if (state?.LastSuccessAt > now - RefreshInterval)
            {
                continue;
            }

            await RefreshAsync(source, definition, states, store, now, cancellationToken);
        }
    }

    private async Task MarkUnconfiguredAsync(
        PricingSourceDefinition definition,
        SourceSyncStateStore states,
        Instant now,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await states.MarkUnconfiguredAsync(
                definition.SourceId,
                definition.ExpectedRefreshInterval,
                now,
                cancellationToken
            );
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogStateWriteFailure(definition.SourceId, exception, "pricing state");
        }
    }

    private async Task RefreshAsync(
        IPricingSource source,
        PricingSourceDefinition definition,
        SourceSyncStateStore states,
        PricingSnapshotStore store,
        Instant now,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await states.MarkAttemptAsync(source.SourceId, definition.ExpectedRefreshInterval, now, cancellationToken);
            var candidate = await source.FetchAsync(cancellationToken);
            if (candidate is not null)
            {
                // Task 5 must supply the transaction-local repricing callback before this pricing plan is complete.
                await store.ActivateAsync(candidate, cancellationToken);
            }
            await states.MarkSuccessAsync(
                source.SourceId,
                definition.ExpectedRefreshInterval,
                now,
                candidate?.RetrievedAt,
                cancellationToken
            );
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await PersistFailureAsync(source.SourceId, definition, states, now, exception, cancellationToken);
        }
    }

    private async Task PersistFailureAsync(
        string sourceId,
        PricingSourceDefinition definition,
        SourceSyncStateStore states,
        Instant now,
        Exception exception,
        CancellationToken cancellationToken
    )
    {
        var error = ProviderPollingWorkerService.SanitizeError(exception.Message);
        try
        {
            await states.MarkFailureAsync(sourceId, definition.ExpectedRefreshInterval, now, error, cancellationToken);
            logger.LogError("{SourceId} pricing refresh failed: {Error}", sourceId, error);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception stateException)
        {
            LogStateWriteFailure(sourceId, stateException, "pricing failure state");
        }
    }

    private void LogStateWriteFailure(string sourceId, Exception exception, string operation) =>
        logger.LogError(
            "{SourceId} {Operation} could not be persisted: {Error}",
            sourceId,
            operation,
            ProviderPollingWorkerService.SanitizeError(exception.Message)
        );
}
