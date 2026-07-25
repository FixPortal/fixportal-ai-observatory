using AiObservatory.Data.Pricing;
using AwesomeAssertions;
using NodaTime;

namespace AiObservatory.Ingest.Tests.Services;

/// <summary>
/// Covers <see cref="AnthropicPricingResolver"/> directly, rather than only through
/// <see cref="AiObservatory.Ingest.Services.Anthropic.AnthropicUsageClient"/>.
/// </summary>
/// <remarks>
/// The resolver lives in AiObservatory.Data but is tested here: AiObservatory.Data.Tests
/// requires a local PostgreSQL instance, and these are pure functions that should stay
/// runnable in the pre-push gate without one.
/// <para>
/// The ordering cases are not hypothetical. An adversarial review split on whether sorting
/// by prefix length ahead of the dated tiebreak defeats date windows; it does not, because
/// the date filter runs first — <see cref="OutOfWindowEntryNeverWinsOnPrefixLength"/> pins
/// that behaviour so the question does not have to be re-litigated by reading the LINQ.
/// </para>
/// </remarks>
public class AnthropicPricingResolverTests
{
    private static readonly LocalDate InIntroWindow = new(2026, 7, 25);
    private static readonly LocalDate AfterIntroWindow = new(2026, 9, 15);

    private static AnthropicPricingOptions Options() => new()
    {
        Pricing =
        [
            // A dated pair for the same prefix — an introductory price and its successor.
            new AnthropicPricingEntry("claude-sonnet-5", 2.0m, 10.0m, 0.20m, 2.50m, EffectiveTo: new LocalDate(2026, 8, 31)),
            new AnthropicPricingEntry("claude-sonnet-5", 3.0m, 15.0m, 0.30m, 3.75m, EffectiveFrom: new LocalDate(2026, 9, 1)),
            // Overlapping prefixes of different lengths — the longer must win.
            new AnthropicPricingEntry("claude-opus-4", 15.0m, 75.0m, 1.50m, 18.75m),
            new AnthropicPricingEntry("claude-opus-4-8", 5.0m, 25.0m, 0.50m, 6.25m),
        ],
        FallbackPricing = new PricingRates4(3.0m, 15.0m, 0.30m, 3.75m),
    };

    [Theory]
    // The longer prefix wins, so Opus 4.8 is not priced as a legacy Opus 4.
    [InlineData("claude-opus-4-8", 5.0, 25.0)]
    [InlineData("claude-opus-4-1", 15.0, 75.0)]
    // Matching is by prefix, so a dated model id resolves to its family's row.
    [InlineData("claude-opus-4-8-20260115", 5.0, 25.0)]
    // ...and is case-insensitive.
    [InlineData("CLAUDE-OPUS-4-8", 5.0, 25.0)]
    public void LongestMatchingPrefixWins(string model, double expectedInput, double expectedOutput)
    {
        var rates = Options().ResolveRates(model, InIntroWindow);

        rates.Input.Should().Be((decimal)expectedInput);
        rates.Output.Should().Be((decimal)expectedOutput);
    }

    [Theory]
    // Inside the introductory window the bounded entry applies...
    [InlineData(2026, 7, 25, 2.0, 10.0)]
    [InlineData(2026, 8, 31, 2.0, 10.0)]   // inclusive upper bound
    // ...and outside it, its successor does.
    [InlineData(2026, 9, 1, 3.0, 15.0)]    // inclusive lower bound
    [InlineData(2026, 12, 1, 3.0, 15.0)]
    public void DateWindowSelectsTheApplicableRow(int year, int month, int day, double expectedInput, double expectedOutput)
    {
        var rates = Options().ResolveRates("claude-sonnet-5", new LocalDate(year, month, day));

        rates.Input.Should().Be((decimal)expectedInput);
        rates.Output.Should().Be((decimal)expectedOutput);
    }

    [Fact]
    public void OutOfWindowEntryNeverWinsOnPrefixLength()
    {
        // A LONGER prefix whose window has closed, against a shorter always-on entry.
        // Ordering alone would pick the longer one; the date filter must eliminate it first.
        var options = new AnthropicPricingOptions
        {
            Pricing =
            [
                new AnthropicPricingEntry("claude-sonnet-5-preview", 1.0m, 5.0m, 0.10m, 1.25m, EffectiveTo: new LocalDate(2026, 6, 30)),
                new AnthropicPricingEntry("claude-sonnet-5", 3.0m, 15.0m, 0.30m, 3.75m),
            ],
            FallbackPricing = new PricingRates4(99m, 99m, 99m, 99m),
        };

        var rates = options.ResolveRates("claude-sonnet-5-preview", AfterIntroWindow);

        rates.Input.Should().Be(3.0m, "the expired longer-prefix entry must be filtered out before ordering");
    }

    [Fact]
    public void DatedEntryBeatsUndatedOnEqualPrefixLength()
    {
        var options = new AnthropicPricingOptions
        {
            Pricing =
            [
                new AnthropicPricingEntry("claude-sonnet-5", 3.0m, 15.0m, 0.30m, 3.75m),
                new AnthropicPricingEntry("claude-sonnet-5", 2.0m, 10.0m, 0.20m, 2.50m, EffectiveTo: new LocalDate(2026, 8, 31)),
            ],
            FallbackPricing = new PricingRates4(99m, 99m, 99m, 99m),
        };

        var rates = options.ResolveRates("claude-sonnet-5", InIntroWindow);

        rates.Input.Should().Be(2.0m, "a bounded entry is more specific than an always-on one");
    }

    [Fact]
    public void UnknownModelYieldsNoMatchAndFallsBackToFallbackRates()
    {
        var options = Options();

        options.Match("gpt-5.4", InIntroWindow).Should().BeNull();
        options.ResolveRates("gpt-5.4", InIntroWindow).Should().Be(options.FallbackPricing);
    }

    [Fact]
    public void ComputeCostPricesEachTokenClassAtItsOwnRate()
    {
        var rates = new PricingRates4(Input: 5.0m, Output: 25.0m, CacheRead: 0.50m, CacheWrite: 6.25m);

        // One million of each class, so the cost is simply the sum of the four rates.
        var cost = AnthropicPricingResolver.ComputeCost(rates, 1_000_000, 1_000_000, 1_000_000, 1_000_000);

        cost.Should().Be(36.75m);
    }

    [Fact]
    public void ComputeCostIsZeroForZeroTokens()
    {
        var rates = new PricingRates4(5.0m, 25.0m, 0.50m, 6.25m);

        AnthropicPricingResolver.ComputeCost(rates, 0, 0, 0, 0).Should().Be(0m);
    }
}
