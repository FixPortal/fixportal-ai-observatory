using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using NodaTime;
// ReSharper disable NotAccessedPositionalProperty.Local; unused fields kept for shape-fidelity with the API response.

namespace AiObservatory.Ingest.Services.OpenAi;

// Calls GET https://api.openai.com/v1/organization/usage/completions
// Requires an admin API key (OPENAI_ADMIN_KEY env var) with the
// openai.usage.read permission (create one at platform.openai.com/api-keys).
// See https://platform.openai.com/docs/api-reference/usage for the schema.
public class OpenAiUsageClient(HttpClient http, ILogger<OpenAiUsageClient> logger, IOptions<OpenAiPricingOptions> pricingOptions) : IOpenAiUsageClient
{
    // Requesting more pages than this for a single day's usage indicates the pagination
    // token is not advancing (e.g. an API change) — bail rather than loop unbounded.
    // Mirrors AnthropicUsageClient.MaxPages.
    private const int MaxPages = 100;

    public async Task<IReadOnlyList<OpenAiUsageRecord>> GetDailyUsageAsync(
        LocalDate date, CancellationToken ct = default)
    {
        var startTime = date.AtStartOfDayInZone(DateTimeZone.Utc).ToInstant().ToUnixTimeSeconds();
        var endTime = date.PlusDays(1).AtStartOfDayInZone(DateTimeZone.Utc).ToInstant().ToUnixTimeSeconds();

        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        var allRecords = new List<OpenAiUsageRecord>();

        string? nextPage = null;
        bool hasMore = true;
        int page = 0;

        while (hasMore)
        {
            if (++page > MaxPages)
            {
                logger.LogWarning("OpenAI usage pagination exceeded {MaxPages} pages for {Date}; stopping", MaxPages, date);
                break;
            }

            var url = $"/v1/organization/usage/completions?start_time={startTime}&end_time={endTime}&bucket_width=1d&group_by[]=model&limit=100";
            if (!string.IsNullOrEmpty(nextPage))
            {
                url += $"&page={nextPage}";
            }

            var response = await http.GetFromJsonAsync<OpenAiUsageApiResponse>(url, options, ct);

            foreach (var bucket in response?.Data ?? [])
            {
                foreach (var result in bucket.Results ?? [])
                {
                    if (string.IsNullOrEmpty(result.Model))
                    { continue; }

                    var cost = ComputeCost(result.Model, date, result.InputTokens, result.OutputTokens, result.InputCachedTokens);

                    allRecords.Add(new OpenAiUsageRecord(
                        Date: date,
                        Model: result.Model,
                        InputTokens: result.InputTokens,
                        OutputTokens: result.OutputTokens,
                        CachedInputTokens: result.InputCachedTokens,
                        CostUsd: cost,
                        RawJson: JsonSerializer.Serialize(result, options)));
                }
            }

            hasMore = response?.HasMore == true && !string.IsNullOrEmpty(response.NextPage);
            nextPage = response?.NextPage;
        }

        return allRecords;
    }

    private decimal ComputeCost(string model, LocalDate usageDate, long input, long output, long cachedInput)
    {
        // Longest matching prefix wins. The OpenAI usage API returns ids like
        // "gpt-4o-mini-2024-07-18"; StartsWith + longest key resolves to the specific
        // variant rather than the base model (see git history for the Contains bug this
        // replaced). Among ties for a given date, a dated entry beats an always-on one.
        var match = pricingOptions.Value.Pricing
            .Where(e => model.StartsWith(e.ModelPrefix, StringComparison.OrdinalIgnoreCase))
            .Where(e => (e.EffectiveFrom is null || usageDate >= e.EffectiveFrom)
                     && (e.EffectiveTo is null || usageDate <= e.EffectiveTo))
            .OrderByDescending(e => e.ModelPrefix.Length)
            .ThenByDescending(e => e.EffectiveFrom is not null || e.EffectiveTo is not null)
            .FirstOrDefault();

        // Surface an unrecognised model instead of silently billing it at the fallback rate.
        if (match is null)
        {
            logger.LogWarning("No OpenAI pricing entry for model '{Model}'; using fallback rates. Add an explicit entry to keep cost accurate.", model);
        }

        var (ir, or, cr) = match is null
            ? pricingOptions.Value.FallbackPricing
            : new PricingRates3(match.Input, match.Output, match.CacheRead);

        // Cached tokens are billed at the cache read rate; non-cached at the full input rate
        var billableInput = Math.Max(0, input - cachedInput);
        return billableInput / 1_000_000m * ir
             + output / 1_000_000m * or
             + cachedInput / 1_000_000m * cr;
    }

    private sealed record OpenAiUsageApiResponse(List<OpenAiUsageBucket>? Data, bool? HasMore, string? NextPage);
    private sealed record OpenAiUsageBucket(long StartTime, long EndTime, List<OpenAiUsageResult>? Results);
    private sealed record OpenAiUsageResult(
        string? Model,
        long InputTokens,
        long OutputTokens,
        long InputCachedTokens,
        long NumModelRequests);
}
