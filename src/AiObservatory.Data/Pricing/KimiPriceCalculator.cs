using AiObservatory.Data.Entities;
using AiObservatory.Data.Pricing.Catalogs;

namespace AiObservatory.Data.Pricing;

public sealed class KimiPriceCalculator : IProviderPriceCalculator
{
    public Provider Provider => Provider.Moonshot;

    public UsagePriceQuote? Calculate(UsageEvent usage, string normalizedCatalog)
    {
        if (string.IsNullOrWhiteSpace(usage.Model))
        {
            return null;
        }

        using var evidence = ProviderPricingJson.Evidence(usage.RawPayload);
        if (
            !ProviderPricingJson.TryBoolean(evidence.RootElement, "high_speed", out var highSpeed)
            || !ProviderPricingJson.TryBoolean(evidence.RootElement, "batch", out var batch)
        )
        {
            return null;
        }

        var entry = PricingCatalogJson
            .Deserialize<KimiPriceCatalog>(normalizedCatalog)
            .Resolve(usage.Model, highSpeed, usage.OccurredAt.InUtc().Date);
        if (entry is null || batch && entry.BatchMultiplier is null)
        {
            return null;
        }

        var multiplier = batch ? entry.BatchMultiplier!.Value : 1m;
        var cacheRead = usage.CacheReadTokens ?? 0;
        var cacheMiss = usage.InputTokens + (usage.CacheWriteTokens ?? 0);
        var cost =
            (
                PerMillion(cacheMiss, entry.CacheMiss)
                + PerMillion(cacheRead, entry.CacheHit)
                + PerMillion(usage.OutputTokens, entry.Output)
            ) * multiplier;
        var savings = PerMillion(cacheRead, entry.CacheMiss - entry.CacheHit) * multiplier;
        return new UsagePriceQuote(cost, savings);
    }

    private static decimal PerMillion(long tokens, decimal rate) => tokens / 1_000_000m * rate;
}
