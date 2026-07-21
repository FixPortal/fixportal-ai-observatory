using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Text;
// ReSharper disable NotAccessedPositionalProperty.Local; unused fields kept for shape-fidelity with the API response.

namespace AiObservatory.Ingest.Services.Anthropic;

// Calls GET https://api.anthropic.com/v1/organizations/usage_report/messages
// Requires an API key with workspace admin access (ANTHROPIC_BILLING_KEY env var).
// See https://docs.anthropic.com/en/api/usage for the current response schema.
public class AnthropicUsageClient(HttpClient http, ILogger<AnthropicUsageClient> logger, IOptions<AnthropicPricingOptions> pricingOptions) : IAnthropicUsageClient
{
    // Requesting more pages than this for a single day's usage indicates the pagination
    // token is not advancing (e.g. an API change) — bail rather than loop unbounded.
    private const int MaxPages = 100;

    public async Task<IReadOnlyList<AnthropicUsageRecord>> GetUsageAsync(
        LocalDate date, CancellationToken ct = default)
    {
        var startInstant = date.AtStartOfDayInZone(DateTimeZone.Utc).ToInstant();
        var endInstant = date.PlusDays(1).AtStartOfDayInZone(DateTimeZone.Utc).ToInstant();
        var startStr = startInstant.ToString();
        var endStr = endInstant.ToString();

        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        var allRecords = new List<AnthropicUsageRecord>();

        string? nextPage = null;
        bool hasMore = true;
        int page = 0;

        while (hasMore)
        {
            if (++page > MaxPages)
            {
                logger.LogWarning("Anthropic usage pagination exceeded {MaxPages} pages for {Date}; stopping", MaxPages, date);
                break;
            }

            var url = $"/v1/organizations/usage_report/messages?starting_at={startStr}&ending_at={endStr}&bucket_width=1d&group_by[]=model";
            if (!string.IsNullOrEmpty(nextPage))
            {
                // The response's next_page token is passed back as the `page` request parameter.
                url += $"&page={nextPage}";
            }

            var response = await http.GetFromJsonAsync<AnthropicUsageApiResponse>(url, options, ct);
            foreach (var bucket in response?.Data ?? [])
            {
                AddBucketRecords(bucket, date, options, allRecords);
            }

            hasMore = response?.HasMore == true && !string.IsNullOrEmpty(response.NextPage);
            nextPage = response?.NextPage;
        }

        return allRecords;
    }

    private void AddBucketRecords(
        AnthropicUsageBucket bucket,
        LocalDate fallbackDate,
        JsonSerializerOptions options,
        ICollection<AnthropicUsageRecord> records)
    {
        var date = ParseBucketDate(bucket.StartingAt, fallbackDate);

        // The usage report nests per-model token counts inside each bucket's
        // results[] array; the bucket itself carries only the time window.
        foreach (var result in bucket.Results ?? [])
        {
            var model = result.Model ?? "unknown";
            var costUsd = ComputeCost(model, date, result.InputTokens, result.OutputTokens,
                result.CacheReadInputTokens, result.CacheCreationInputTokens);

            records.Add(new AnthropicUsageRecord(
                Date: date,
                Model: model,
                InputTokens: result.InputTokens,
                OutputTokens: result.OutputTokens,
                CacheReadTokens: result.CacheReadInputTokens,
                CacheWriteTokens: result.CacheCreationInputTokens,
                CostUsd: costUsd,
                RawJson: JsonSerializer.Serialize(result, options)));
        }
    }

    private static LocalDate ParseBucketDate(string? startingAt, LocalDate fallback)
    {
        if (startingAt is not { Length: >= 10 })
        {
            return fallback;
        }

        var parsed = LocalDatePattern.Iso.Parse(startingAt[..10]);
        return parsed.Success ? parsed.Value : fallback;
    }

    private decimal ComputeCost(string model, LocalDate usageDate, long input, long output, long cacheRead, long cacheWrite)
    {
        // Longest matching prefix wins; among ties for a given date, a dated (bounded)
        // entry beats an always-on one — this is how the Sonnet-5 intro-pricing window
        // is expressed as data instead of a special-cased branch.
        var match = pricingOptions.Value.Pricing
            .Where(e => model.StartsWith(e.ModelPrefix, StringComparison.OrdinalIgnoreCase))
            .Where(e => (e.EffectiveFrom is null || usageDate >= e.EffectiveFrom)
                     && (e.EffectiveTo is null || usageDate <= e.EffectiveTo))
            .OrderByDescending(e => e.ModelPrefix.Length)
            .ThenByDescending(e => e.EffectiveFrom is not null || e.EffectiveTo is not null)
            .FirstOrDefault();

        if (match is null)
        {
            // Unknown model — surface it instead of silently mis-costing at the fallback rate.
            logger.LogWarning("No Anthropic pricing entry for model '{Model}'; using fallback rates. Add an explicit entry to keep cost accurate.", model);
        }

        var (ir, or, crr, cwr) = match is null
            ? pricingOptions.Value.FallbackPricing
            : new PricingRates4(match.Input, match.Output, match.CacheRead, match.CacheWrite);

        return input / 1_000_000m * ir + output / 1_000_000m * or
             + cacheRead / 1_000_000m * crr + cacheWrite / 1_000_000m * cwr;
    }

    private sealed record AnthropicUsageApiResponse(List<AnthropicUsageBucket>? Data, bool? HasMore, string? NextPage);
    private sealed record AnthropicUsageBucket(string? StartingAt, string? EndingAt, List<AnthropicUsageResult>? Results);
    private sealed record AnthropicUsageResult(
        string? Model,
        long InputTokens,
        long OutputTokens,
        long CacheReadInputTokens,
        long CacheCreationInputTokens);
}
