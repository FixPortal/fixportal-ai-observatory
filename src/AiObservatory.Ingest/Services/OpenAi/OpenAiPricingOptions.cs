using NodaTime;

namespace AiObservatory.Ingest.Services.OpenAi;

public class OpenAiPricingOptions
{
    public const string SectionName = "Ingest:OpenAi";

    public List<OpenAiPricingEntry> Pricing { get; set; } = [];
    public PricingRates3 FallbackPricing { get; set; } = new(2.50m, 10.00m, 1.25m);
}

public sealed record OpenAiPricingEntry(
    string ModelPrefix,
    decimal Input,
    decimal Output,
    decimal CacheRead,
    LocalDate? EffectiveFrom = null,
    LocalDate? EffectiveTo = null);

public sealed record PricingRates3(decimal Input, decimal Output, decimal CacheRead);
