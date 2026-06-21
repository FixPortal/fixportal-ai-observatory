using System.Net.Http.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

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

    private sealed record FrankfurterResponse(Dictionary<string, decimal>? Rates);
}
