using NodaTime;

namespace AiObservatory.Data.Pricing;

// Anthropic pricing lives in AiObservatory.Data, not in the Ingest worker, because more
// than one app needs it: the worker prices what it polls, and the API needs the same
// rates to price events at ingest and to re-cost history. Both projects already reference
// Data, so this needs no new project references.
//
// The section name is still "Ingest:Anthropic" for config-compatibility with existing
// appsettings and deployed configuration. Renaming it to "Pricing:Anthropic" would drag
// the OpenAI section along for consistency; deferred deliberately.
public class AnthropicPricingOptions
{
    public const string SectionName = "Ingest:Anthropic";

    public List<AnthropicPricingEntry> Pricing { get; set; } = [];
    public PricingRates FallbackPricing { get; set; } = new(3.0m, 15.0m, 0.30m, 3.75m, 6.0m);
}

/// <param name="CacheWrite">Rate for a FIVE-MINUTE cache write.</param>
/// <param name="CacheWrite1h">
/// Rate for a ONE-HOUR cache write. Anthropic bills the two TTLs differently (1.25x vs
/// 2x base input), so a deployment that writes exclusively one-hour entries is understated
/// by ~60% on the cache-write line if only the five-minute rate exists.
/// </param>
public sealed record AnthropicPricingEntry(
    string ModelPrefix,
    decimal Input,
    decimal Output,
    decimal CacheRead,
    decimal CacheWrite,
    decimal CacheWrite1h,
    LocalDate? EffectiveFrom = null,
    LocalDate? EffectiveTo = null);

public sealed record PricingRates(
    decimal Input,
    decimal Output,
    decimal CacheRead,
    decimal CacheWrite,
    decimal CacheWrite1h);

/// <summary>
/// Resolves a model id and usage date to a rate row.
/// </summary>
/// <remarks>
/// Extracted from <c>AnthropicUsageClient.ComputeCost</c> so that ingest-time costing and
/// any historical re-cost resolve identically. Two implementations of this algorithm is
/// exactly the drift that produced the pricing defects this type exists to prevent.
/// </remarks>
public static class AnthropicPricingResolver
{
    /// <summary>
    /// Finds the rate row for <paramref name="model"/> on <paramref name="usageDate"/>,
    /// or <c>null</c> when nothing matches.
    /// </summary>
    /// <remarks>
    /// Order matters, and the date filter deliberately runs FIRST: entries outside the
    /// usage date's window are eliminated before any ordering, so an out-of-window row can
    /// never win on prefix length. Among the surviving candidates the longest prefix wins
    /// (so <c>claude-opus-4-8</c> beats <c>claude-opus-4</c>), and a dated entry beats an
    /// undated one on a tie — which is how a temporary introductory price is expressed as
    /// data rather than as a special case.
    /// </remarks>
    public static AnthropicPricingEntry? Match(
        this AnthropicPricingOptions options,
        string model,
        LocalDate usageDate) =>
        options.Pricing
            .Where(e => model.StartsWith(e.ModelPrefix, StringComparison.OrdinalIgnoreCase))
            .Where(e => (e.EffectiveFrom is null || usageDate >= e.EffectiveFrom)
                     && (e.EffectiveTo is null || usageDate <= e.EffectiveTo))
            .OrderByDescending(e => e.ModelPrefix.Length)
            .ThenByDescending(e => e.EffectiveFrom is not null || e.EffectiveTo is not null)
            .FirstOrDefault();

    /// <summary>The rate columns of a matched entry.</summary>
    /// <remarks>
    /// Lets a caller that already holds a <see cref="Match"/> result — because it wants to
    /// log or branch on a miss — turn it into rates without resolving a second time.
    /// </remarks>
    public static PricingRates ToRates(this AnthropicPricingEntry entry) =>
        new(entry.Input, entry.Output, entry.CacheRead, entry.CacheWrite, entry.CacheWrite1h);

    /// <summary>
    /// Rates for <paramref name="model"/> on <paramref name="usageDate"/>, falling back to
    /// <see cref="AnthropicPricingOptions.FallbackPricing"/> when no entry matches.
    /// </summary>
    public static PricingRates ResolveRates(
        this AnthropicPricingOptions options,
        string model,
        LocalDate usageDate) =>
        options.Match(model, usageDate)?.ToRates() ?? options.FallbackPricing;

    /// <summary>Cost in USD for a token quantity at the given rates (rates are per million).</summary>
    /// <param name="cacheWriteTokens">ALL cache-write tokens, both TTLs.</param>
    /// <param name="cacheWrite1hTokens">
    /// The one-hour subset of <paramref name="cacheWriteTokens"/>. The remainder is billed at
    /// the five-minute rate. Callers with no TTL breakdown (the Anthropic usage-report API does
    /// not publish one) pass 0, which prices everything at the five-minute rate as before.
    /// </param>
    public static decimal ComputeCost(
        PricingRates rates,
        long inputTokens,
        long outputTokens,
        long cacheReadTokens,
        long cacheWriteTokens,
        long cacheWrite1hTokens = 0)
    {
        // Clamped rather than asserted: this is a money path, and the transcripts it is fed
        // from are not perfectly self-consistent. Measured over 281,354 cache-bearing
        // assistant messages, 3 report a 1h count fractionally above their own total. The
        // API rejects that shape at the boundary, so this only guards a caller that bypasses
        // it -- at the cost of pricing the overflow as 1h, the dearer of the two.
        var cacheWrite5m = Math.Max(0L, cacheWriteTokens - cacheWrite1hTokens);
        return inputTokens / 1_000_000m * rates.Input
            + outputTokens / 1_000_000m * rates.Output
            + cacheReadTokens / 1_000_000m * rates.CacheRead
            + cacheWrite5m / 1_000_000m * rates.CacheWrite
            + cacheWrite1hTokens / 1_000_000m * rates.CacheWrite1h;
    }
}
