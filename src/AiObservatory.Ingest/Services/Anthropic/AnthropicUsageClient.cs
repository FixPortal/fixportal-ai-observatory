using System.Net.Http.Json;
using System.Text.Json;
using NodaTime;
using NodaTime.Text;

namespace AiObservatory.Ingest.Services.Anthropic;

// Calls GET https://api.anthropic.com/v1/organizations/usage_report/messages
// Requires an API key with workspace admin access (ANTHROPIC_BILLING_KEY env var).
// See https://docs.anthropic.com/en/api/usage for the current response schema.
public class AnthropicUsageClient(HttpClient http) : IAnthropicUsageClient
{
    // Per-1M token rates (USD). Anthropic usage API returns token counts but not cost.
    private static readonly Dictionary<string, (decimal Input, decimal Output, decimal CacheRead, decimal CacheWrite)> Pricing = new()
    {
        ["claude-3-5-opus"] = (15.0m, 75.0m, 1.50m, 18.75m),
        ["claude-3-5-sonnet"] = (3.0m, 15.0m, 0.30m, 3.75m),
        ["claude-3-5-haiku"] = (0.8m, 4.0m, 0.08m, 1.00m),
        ["claude-opus-4"] = (15.0m, 75.0m, 1.50m, 18.75m),
        ["claude-sonnet-4"] = (3.0m, 15.0m, 0.30m, 3.75m),
        ["claude-haiku-4"] = (0.8m, 4.0m, 0.08m, 1.00m),
        ["claude-opus-3-5"] = (15.0m, 75.0m, 1.50m, 18.75m),
        ["claude-sonnet-3-5"] = (3.0m, 15.0m, 0.30m, 3.75m),
        ["claude-haiku-3-5"] = (0.8m, 4.0m, 0.08m, 1.00m),
        ["claude-3-opus"] = (15.0m, 75.0m, 1.50m, 18.75m),
        ["claude-3-sonnet"] = (3.0m, 15.0m, 0.30m, 3.75m),
        ["claude-3-haiku"] = (0.25m, 1.25m, 0.03m, 0.3125m),
    };

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

        while (hasMore)
        {
            var url = $"/v1/organizations/usage_report/messages?starting_at={startStr}&ending_at={endStr}&bucket_width=1d&group_by[]=model";
            if (!string.IsNullOrEmpty(nextPage))
            {
                url += $"&next_page={nextPage}";
            }

            var response = await http.GetFromJsonAsync<AnthropicUsageApiResponse>(url, options, ct);
            if (response?.Data != null)
            {
                foreach (var d in response.Data)
                {
                    LocalDate parsedDate = date;
                    if (d.BucketStartTime != null && d.BucketStartTime.Length >= 10)
                    {
                        var parseResult = LocalDatePattern.Iso.Parse(d.BucketStartTime[..10]);
                        if (parseResult.Success)
                        {
                            parsedDate = parseResult.Value;
                        }
                    }

                    var cacheRead = d.CacheReadInputTokens > 0 ? d.CacheReadInputTokens : d.CachedInputTokens;
                    var costUsd = ComputeCost(d.Model ?? "unknown", d.InputTokens, d.OutputTokens,
                        cacheRead, d.CacheCreationInputTokens);

                    allRecords.Add(new AnthropicUsageRecord(
                        Date: parsedDate,
                        Model: d.Model ?? "unknown",
                        InputTokens: d.InputTokens,
                        OutputTokens: d.OutputTokens,
                        CacheReadTokens: cacheRead,
                        CacheWriteTokens: d.CacheCreationInputTokens,
                        CostUsd: costUsd,
                        RawJson: JsonSerializer.Serialize(d, options)));
                }
            }

            hasMore = response?.HasMore == true && !string.IsNullOrEmpty(response.NextPage);
            nextPage = response?.NextPage;
        }

        return allRecords;
    }

    private static decimal ComputeCost(string model, long input, long output, long cacheRead, long cacheWrite)
    {
        // Longest matching prefix wins (same fix as OpenAiUsageClient). The tiers here
        // don't currently share price-differing prefixes, but Contains + FirstOrDefault
        // is the same latent trap, so resolve to the most specific key defensively.
        var (ir, or, crr, cwr) = Pricing
            .Where(kv => model.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(kv => kv.Key.Length)
            .Select(kv => kv.Value)
            .FirstOrDefault();
        // Default to sonnet pricing if model not found
        if (ir == 0)
        {
            (ir, or, crr, cwr) = (3.0m, 15.0m, 0.30m, 3.75m);
        }
        return input / 1_000_000m * ir + output / 1_000_000m * or
             + cacheRead / 1_000_000m * crr + cacheWrite / 1_000_000m * cwr;
    }

    private sealed record AnthropicUsageApiResponse(List<AnthropicUsageApiRecord>? Data, bool? HasMore, string? NextPage);
    private sealed record AnthropicUsageApiRecord(
        string? BucketStartTime,
        string? Model,
        long InputTokens,
        long OutputTokens,
        long CacheReadInputTokens,
        long CachedInputTokens,
        long CacheCreationInputTokens);
}
