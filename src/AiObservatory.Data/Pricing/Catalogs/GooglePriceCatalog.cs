using System.Text.Json.Serialization;
using NodaTime;

namespace AiObservatory.Data.Pricing.Catalogs;

public sealed record GooglePriceCatalog(
    string Currency,
    string SourceUrl,
    Instant RetrievedAt,
    IReadOnlyList<GooglePriceEntry> Entries
)
{
    public void Validate()
    {
        if (
            Currency != "USD"
            || !Uri.TryCreate(SourceUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
        )
        {
            throw new InvalidDataException("Google pricing must be USD and have an HTTPS source URL.");
        }

        if (Entries is null)
        {
            throw new InvalidDataException("Google pricing entries are required.");
        }

        var effectiveDates = new Dictionary<string, LocalDate>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Entries)
        {
            if (
                string.IsNullOrWhiteSpace(entry.Service)
                || string.IsNullOrWhiteSpace(entry.SkuId)
                || string.IsNullOrWhiteSpace(entry.SkuName)
                || string.IsNullOrWhiteSpace(entry.Description)
                || entry.Aliases is null
                || entry.Aliases.Count == 0
                || entry.Aliases.Any(string.IsNullOrWhiteSpace)
                || entry.Aliases.Distinct(StringComparer.OrdinalIgnoreCase).Count() != entry.Aliases.Count
                || string.IsNullOrWhiteSpace(entry.Region)
                || string.IsNullOrWhiteSpace(entry.Modality)
                || string.IsNullOrWhiteSpace(entry.Tier)
                || string.IsNullOrWhiteSpace(entry.CacheLane)
                || entry.ContextThreshold < 0
                || string.IsNullOrWhiteSpace(entry.PricingUnit)
                || string.IsNullOrWhiteSpace(entry.PricingUnitDescription)
                || string.IsNullOrWhiteSpace(entry.BaseUnit)
                || string.IsNullOrWhiteSpace(entry.BaseUnitDescription)
                || entry.BaseUnitConversionFactor <= 0
                || entry.DisplayQuantity <= 0
                || string.IsNullOrWhiteSpace(entry.GeoTaxonomyType)
                || entry.ServiceRegions is null
                || entry.ServiceRegions.Count == 0
                || entry.ServiceRegions.Any(string.IsNullOrWhiteSpace)
                || entry.GeoTaxonomyRegions is null
                || entry.GeoTaxonomyRegions.Count == 0
                || entry.GeoTaxonomyRegions.Any(string.IsNullOrWhiteSpace)
                || string.IsNullOrWhiteSpace(entry.UnitPriceCurrencyCode)
                || string.IsNullOrWhiteSpace(entry.AggregationLevel)
                || string.IsNullOrWhiteSpace(entry.AggregationInterval)
                || entry.AggregationCount <= 0
                || entry.Rate <= 0
                || entry.EffectiveTime.InUtc().Date != entry.EffectiveFrom
                || entry.UnitPriceCurrencyCode != Currency
                || entry.UnitPriceNanos is < -999_999_999 or > 999_999_999
                || Math.Sign(entry.UnitPriceUnits) * Math.Sign(entry.UnitPriceNanos) < 0
                || entry.Rate != (entry.UnitPriceUnits + entry.UnitPriceNanos / 1_000_000_000m) * 1_000_000m
            )
            {
                throw new InvalidDataException("Google pricing contains an incomplete or non-positive entry.");
            }

            foreach (var alias in entry.Aliases.Prepend(entry.Service).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var key = string.Join(
                    '\u001f',
                    alias,
                    entry.SkuId,
                    entry.Region,
                    entry.Modality,
                    entry.Tier,
                    entry.CacheLane,
                    entry.ContextThreshold
                );
                if (effectiveDates.TryGetValue(key, out var previous) && entry.EffectiveFrom <= previous)
                {
                    throw new InvalidDataException("Google effective windows must be unique and ordered.");
                }

                effectiveDates[key] = entry.EffectiveFrom;
            }
        }
    }

    public GooglePriceEntry? Resolve(
        string service,
        string skuId,
        string region,
        string modality,
        string tier,
        string cacheLane,
        long contextThreshold,
        LocalDate usageDate
    )
    {
        return Entries
            .Where(entry =>
                entry.EffectiveFrom <= usageDate
                && entry
                    .Aliases.Prepend(entry.Service)
                    .Any(alias => string.Equals(alias, service, StringComparison.OrdinalIgnoreCase))
                && string.Equals(entry.SkuId, skuId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(entry.Region, region, StringComparison.OrdinalIgnoreCase)
                && string.Equals(entry.Modality, modality, StringComparison.OrdinalIgnoreCase)
                && string.Equals(entry.Tier, tier, StringComparison.OrdinalIgnoreCase)
                && string.Equals(entry.CacheLane, cacheLane, StringComparison.OrdinalIgnoreCase)
                && entry.ContextThreshold == contextThreshold
            )
            .OrderByDescending(entry => entry.EffectiveFrom)
            .FirstOrDefault();
    }
}

[method: JsonConstructor]
public sealed record GooglePriceEntry(
    string Service,
    string SkuId,
    string SkuName,
    string Description,
    IReadOnlyList<string> Aliases,
    LocalDate EffectiveFrom,
    bool EffectiveDateIsProviderDeclared,
    Instant EffectiveTime,
    string Region,
    string GeoTaxonomyType,
    IReadOnlyList<string> ServiceRegions,
    IReadOnlyList<string> GeoTaxonomyRegions,
    string Modality,
    string Tier,
    string CacheLane,
    long ContextThreshold,
    string PricingUnit,
    string PricingUnitDescription,
    string BaseUnit,
    string BaseUnitDescription,
    decimal BaseUnitConversionFactor,
    decimal DisplayQuantity,
    decimal TierStartUsageAmount,
    string UnitPriceCurrencyCode,
    long UnitPriceUnits,
    int UnitPriceNanos,
    string AggregationLevel,
    string AggregationInterval,
    int AggregationCount,
    decimal CurrencyConversionRate,
    decimal Rate
)
{
    public GooglePriceEntry(
        string service,
        string skuId,
        IReadOnlyList<string> aliases,
        LocalDate effectiveFrom,
        bool effectiveDateIsProviderDeclared,
        string region,
        string modality,
        string tier,
        string cacheLane,
        long contextThreshold,
        string pricingUnit,
        string aggregationLevel,
        decimal rate
    )
        : this(
            service,
            skuId,
            $"services/legacy/skus/{skuId}",
            service,
            aliases,
            effectiveFrom,
            effectiveDateIsProviderDeclared,
            effectiveFrom.AtMidnight().InZoneStrictly(DateTimeZone.Utc).ToInstant(),
            region,
            "REGIONAL",
            [region],
            [region],
            modality,
            tier,
            cacheLane,
            contextThreshold,
            pricingUnit,
            pricingUnit,
            pricingUnit,
            pricingUnit,
            1m,
            1m,
            0m,
            "USD",
            0,
            checked((int)(rate * 1000m)),
            aggregationLevel,
            "DAILY",
            1,
            1m,
            rate
        ) { }
}
