using AiObservatory.Data.Entities;
using AiObservatory.Data.Pricing;
using AiObservatory.Data.Pricing.Catalogs;
using AiObservatory.Ingest.Sources;

namespace AiObservatory.Ingest.Pricing;

public sealed class BundledPricingCatalogLoader
{
    private readonly PricingSnapshotStore _store;
    private readonly PricingRepricingService _repricing;
    private readonly ILogger<BundledPricingCatalogLoader> _logger;
    private readonly string _baseDirectory;

    public BundledPricingCatalogLoader(
        PricingSnapshotStore store,
        PricingRepricingService repricing,
        ILogger<BundledPricingCatalogLoader> logger
    )
        : this(store, repricing, logger, AppContext.BaseDirectory) { }

    internal BundledPricingCatalogLoader(
        PricingSnapshotStore store,
        PricingRepricingService repricing,
        ILogger<BundledPricingCatalogLoader> logger,
        string baseDirectory
    )
    {
        _store = store;
        _repricing = repricing;
        _logger = logger;
        _baseDirectory = baseDirectory;
    }

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        await LoadAsync<OpenAiPriceCatalog>(
            Provider.OpenAI,
            PricingSourceIds.OpenAi,
            "openai.json",
            static catalog => catalog.SourceUrl,
            static catalog => catalog.RetrievedAt,
            cancellationToken
        );
        await LoadAsync<AnthropicPriceCatalog>(
            Provider.Anthropic,
            PricingSourceIds.Claude,
            "claude.json",
            static catalog => catalog.SourceUrl,
            static catalog => catalog.RetrievedAt,
            cancellationToken
        );
        await LoadAsync<KimiPriceCatalog>(
            Provider.Moonshot,
            PricingSourceIds.Kimi,
            "kimi.json",
            static catalog => catalog.SourceUrl,
            static catalog => catalog.RetrievedAt,
            cancellationToken
        );
        await LoadAsync<GooglePriceCatalog>(
            Provider.Google,
            PricingSourceIds.GoogleCloudCatalog,
            "google.json",
            static catalog => catalog.SourceUrl,
            static catalog => catalog.RetrievedAt,
            cancellationToken
        );
    }

    private async Task LoadAsync<T>(
        Provider provider,
        string sourceId,
        string fileName,
        Func<T, string> getSourceUrl,
        Func<T, NodaTime.Instant> getRetrievedAt,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var raw = await File.ReadAllTextAsync(
                Path.Combine(_baseDirectory, "Pricing", "Bundled", fileName),
                cancellationToken
            );
            var catalog = PricingCatalogJson.Deserialize<T>(raw);
            var sourceUrl = getSourceUrl(catalog);
            var retrievedAt = getRetrievedAt(catalog);
            var candidate = PricingCandidate.Create(provider, sourceId, retrievedAt, sourceUrl, raw, catalog);

            await _store.ActivateIfMissingAsync(
                candidate,
                cancellationToken,
                (_, callbackCt) => _repricing.RepriceProviderAsync(provider, callbackCt)
            );
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogLoadFailure(sourceId, exception);
        }
    }

    private void LogLoadFailure(string sourceId, Exception exception)
    {
        var error = exception.Message.Replace(_baseDirectory, "<bundle-directory>", StringComparison.OrdinalIgnoreCase);
        _logger.LogError(
            "{SourceId} bundled pricing load failed: {Error}",
            sourceId,
            ProviderPollingWorkerService.SanitizeError(error)
        );
    }
}
