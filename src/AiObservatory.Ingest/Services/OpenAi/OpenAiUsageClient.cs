using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace AiObservatory.Ingest.Services.OpenAi;

// Calls GET https://api.openai.com/v1/organization/usage/completions
// Requires an admin API key (OPENAI_ADMIN_KEY env var) with the
// openai.usage.read permission (create one at platform.openai.com/api-keys).
// See https://platform.openai.com/docs/api-reference/usage for the schema.
public class OpenAiUsageClient(HttpClient http, ILogger<OpenAiUsageClient> logger) : IOpenAiUsageClient
{
    // Per-1M token input rates (USD). OpenAI usage API returns token counts; cost is derived here.
    // Cache savings = (input_rate - cache_read_rate) per 1M = 50% discount across all current models.
    private static readonly Dictionary<string, (decimal Input, decimal Output, decimal CacheRead)> Pricing = new()
    {
        ["gpt-4.1"]      = (2.00m,  8.00m, 0.50m),
        ["gpt-4.1-mini"] = (0.40m,  1.60m, 0.10m),
        ["gpt-4.1-nano"] = (0.10m,  0.40m, 0.025m),
        ["gpt-4o"]       = (2.50m, 10.00m, 1.25m),
        ["gpt-4o-mini"]  = (0.15m,  0.60m, 0.075m),
        ["o1"]           = (15.0m, 60.00m, 7.50m),
        ["o1-mini"]      = (1.10m,  4.40m, 0.55m),
        ["o3"]           = (2.00m,  8.00m, 0.50m),
        ["o3-mini"]      = (1.10m,  4.40m, 0.55m),
        ["o4-mini"]      = (1.10m,  4.40m, 0.275m),
    };

    public async Task<IReadOnlyList<OpenAiUsageRecord>> GetDailyUsageAsync(
        LocalDate date, CancellationToken ct = default)
    {
        var startTime = date.AtStartOfDayInZone(DateTimeZone.Utc).ToInstant().ToUnixTimeSeconds();
        var endTime = date.PlusDays(1).AtStartOfDayInZone(DateTimeZone.Utc).ToInstant().ToUnixTimeSeconds();

        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        var allRecords = new List<OpenAiUsageRecord>();

        string? nextPage = null;
        bool hasMore = true;

        while (hasMore)
        {
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
                    if (string.IsNullOrEmpty(result.Model)) { continue; }

                    var cost = ComputeCost(result.Model, result.InputTokens, result.OutputTokens, result.InputCachedTokens);

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

    private decimal ComputeCost(string model, long input, long output, long cachedInput)
    {
        // Longest matching prefix wins. The OpenAI usage API returns ids like
        // "gpt-4o-mini-2024-07-18"; matching by Contains + FirstOrDefault picked the
        // first/shortest dictionary key the id contained, so "gpt-4o-mini" resolved to
        // "gpt-4o" and every -mini/-nano model was billed at its base model's rate
        // (5-20x over). StartsWith + longest key resolves to the specific variant.
        var match = Pricing
            .Where(kv => model.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(kv => kv.Key.Length)
            .Select(kv => ((decimal, decimal, decimal)?)kv.Value)
            .FirstOrDefault();

        // Surface an unrecognised model instead of silently billing it at gpt-4o rates.
        if (match is null)
        {
            logger.LogWarning("No OpenAI pricing entry for model '{Model}'; defaulting to gpt-4o rates. Add an explicit entry to keep cost accurate.", model);
        }
        var (ir, or, cr) = match ?? (2.50m, 10.00m, 1.25m);

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
