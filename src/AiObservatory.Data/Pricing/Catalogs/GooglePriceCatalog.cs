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
                || string.IsNullOrWhiteSpace(entry.AggregationLevel)
                || entry.Rate <= 0
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
        LocalDate? usageDate = null
    )
    {
        var date = usageDate ?? LocalDate.MaxIsoValue;
        return Entries
            .Where(entry =>
                entry.EffectiveFrom <= date
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

public sealed record GooglePriceEntry(
    string Service,
    string SkuId,
    IReadOnlyList<string> Aliases,
    LocalDate EffectiveFrom,
    bool EffectiveDateIsProviderDeclared,
    string Region,
    string Modality,
    string Tier,
    string CacheLane,
    long ContextThreshold,
    string PricingUnit,
    string AggregationLevel,
    decimal Rate
);
