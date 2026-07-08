using NodaTime;

namespace AiObservatory.Ingest.Services.Anthropic;

public class AnthropicPricingOptions
{
    public const string SectionName = "Ingest:Anthropic";

    public List<AnthropicPricingEntry> Pricing { get; set; } = [];
    public PricingRates4 FallbackPricing { get; set; } = new(3.0m, 15.0m, 0.30m, 3.75m);
}

public sealed record AnthropicPricingEntry(
    string ModelPrefix,
    decimal Input,
    decimal Output,
    decimal CacheRead,
    decimal CacheWrite,
    LocalDate? EffectiveFrom = null,
    LocalDate? EffectiveTo = null);

public sealed record PricingRates4(decimal Input, decimal Output, decimal CacheRead, decimal CacheWrite);
