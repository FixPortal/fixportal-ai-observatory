using Microsoft.Extensions.Caching.Memory;
using NodaTime;

namespace AiObservatory.Api.Services.Fx;

/// <summary>
/// USD->GBP rate from frankfurter.dev (ECB reference rates, free, no key), cached ~12h.
/// Costs are stored USD-native; this converts them for GBP presentation. An FX outage
/// must never break insight generation, so failures fall back to a static rate.
/// </summary>
public class FxRateProvider(HttpClient http, IMemoryCache cache, ILogger<FxRateProvider> logger)
{
    // Static fallback (~ recent USD->GBP) used only when the FX service is unreachable.
    private const decimal Fallback = 0.79m;
    private const string CacheKey = "fx:usd-gbp";

    public virtual async Task<decimal> GetUsdToGbpAsync(CancellationToken ct = default)
    {
        if (cache.TryGetValue(CacheKey, out decimal cached))
        {
            return cached;
        }

        var rate = await FetchGbpRateAsync(
            "https://api.frankfurter.dev/v1/latest?from=USD&to=GBP", "USD->GBP", ct);

        if (rate <= 0m)
        {
            logger.LogWarning("FX USD->GBP unavailable; using fallback {Fallback}", Fallback);
            return Fallback; // not cached — allow a retry on the next call
        }

        cache.Set(CacheKey, rate, TimeSpan.FromHours(12));
        return rate;
    }

    /// <summary>
    /// Rate converting one unit of <paramref name="currency"/> into GBP on
    /// <paramref name="on"/>. The ledger freezes this at write, so a historical total never
    /// drifts with the market. Historical rates are immutable and therefore cached without
    /// expiry, unlike the 12-hour cache on the latest rate.
    /// </summary>
    /// <exception cref="FxUnavailableException">
    /// The rate could not be resolved for a currency other than USD or GBP. USD has a static
    /// fallback because it is the other currency phase-1 entries actually use; every other
    /// currency must fail the write rather than freeze an undetectably wrong rate.
    /// </exception>
    public virtual async Task<decimal> GetGbpRateOnAsync(
        string currency, LocalDate on, CancellationToken ct = default)
    {
        var code = currency.ToUpperInvariant();
        if (code == "GBP")
        {
            return 1m;
        }

        var key = $"fx:{code}-gbp:{on:yyyy-MM-dd}";
        if (cache.TryGetValue(key, out decimal cached))
        {
            return cached;
        }

        var rate = await FetchGbpRateAsync(
            $"https://api.frankfurter.dev/v1/{on:yyyy-MM-dd}?from={code}&to=GBP", $"{code}->GBP on {on:yyyy-MM-dd}", ct);

        if (rate <= 0m)
        {
            if (code == "USD")
            {
                logger.LogWarning("FX {Code}->GBP missing for {Date}; using fallback {Fallback}", code, on, Fallback);
                return Fallback; // not cached — allow a retry
            }

            logger.LogWarning("FX {Code}->GBP unavailable for {Date}", code, on);
            throw new FxUnavailableException(code, on);
        }

        cache.Set(key, rate);   // no expiry: a past date's rate cannot change
        return rate;
    }

    // Shared fetch: returns 0m for both "fetched but no GBP rate" and "request threw",
    // collapsing both failure shapes into one signal so each caller applies its own
    // fallback policy (GetUsdToGbpAsync always falls back; GetGbpRateOnAsync only for USD).
    private async Task<decimal> FetchGbpRateAsync(string url, string label, CancellationToken ct)
    {
        try
        {
            var resp = await http.GetFromJsonAsync<FrankfurterResponse>(url, ct);
            return resp?.Rates is { } r && r.TryGetValue("GBP", out var gbp) ? gbp : 0m;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "FX fetch failed for {Label}", label);
            return 0m;
        }
    }

    private sealed record FrankfurterResponse(Dictionary<string, decimal>? Rates);
}
