using AiObservatory.Data.Entities;
using AiObservatory.Data.Pricing;
using AiObservatory.Data.Pricing.Catalogs;
using AiObservatory.Ingest.Sources;

namespace AiObservatory.Ingest.Pricing;

public sealed class BundledPricingCatalogLoader
{
    private readonly PricingSnapshotStore _store;
    private readonly string _baseDirectory;

    public BundledPricingCatalogLoader(PricingSnapshotStore store)
        : this(store, AppContext.BaseDirectory) { }

    internal BundledPricingCatalogLoader(PricingSnapshotStore store, string baseDirectory)
    {
        _store = store;
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
        var raw = await File.ReadAllTextAsync(
            Path.Combine(_baseDirectory, "Pricing", "Bundled", fileName),
            cancellationToken
        );
        var catalog = PricingCatalogJson.Deserialize<T>(raw);
        var sourceUrl = getSourceUrl(catalog);
        var retrievedAt = getRetrievedAt(catalog);
        var candidate = PricingCandidate.Create(provider, sourceId, retrievedAt, sourceUrl, raw, catalog);

        // Task 5 must supply the transaction-local repricing callback before this pricing plan is complete.
        await _store.ActivateIfMissingAsync(candidate, cancellationToken);
    }
}
