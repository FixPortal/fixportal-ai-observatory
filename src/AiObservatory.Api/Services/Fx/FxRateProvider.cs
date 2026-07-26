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

        try
        {
            var resp = await http.GetFromJsonAsync<FrankfurterResponse>(
                "https://api.frankfurter.dev/v1/latest?from=USD&to=GBP", ct);
            var rate = resp?.Rates is { } rates && rates.TryGetValue("GBP", out var gbp) ? gbp : 0m;

            if (rate <= 0m)
            {
                logger.LogWarning("FX USD->GBP missing/invalid in response; using fallback {Fallback}", Fallback);
                return Fallback; // not cached — allow a retry on the next call
            }

            cache.Set(CacheKey, rate, TimeSpan.FromHours(12));
            return rate;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "FX fetch failed; using fallback {Fallback}", Fallback);
            return Fallback;
        }
    }

    /// <summary>
    /// Rate converting one unit of <paramref name="currency"/> into GBP on
    /// <paramref name="on"/>. The ledger freezes this at write, so a historical total never
    /// drifts with the market. Historical rates are immutable and therefore cached without
    /// expiry, unlike the 12-hour cache on the latest rate.
    /// </summary>
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

        try
        {
            var resp = await http.GetFromJsonAsync<FrankfurterResponse>(
                $"https://api.frankfurter.dev/v1/{on:yyyy-MM-dd}?from={code}&to=GBP", ct);
            var rate = resp?.Rates is { } rates && rates.TryGetValue("GBP", out var gbp) ? gbp : 0m;

            if (rate <= 0m)
            {
                logger.LogWarning("FX {Code}->GBP missing for {Date}; using fallback {Fallback}", code, on, Fallback);
                return Fallback; // not cached — allow a retry
            }

            cache.Set(key, rate);   // no expiry: a past date's rate cannot change
            return rate;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "FX fetch failed for {Code} on {Date}; using fallback {Fallback}", code, on, Fallback);
            return Fallback;
        }
    }

    private sealed record FrankfurterResponse(Dictionary<string, decimal>? Rates);
}
