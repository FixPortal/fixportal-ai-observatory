using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NodaTime;
using NodaTime.Text;

namespace AiObservatory.Ingest.Services.Anthropic;

// Calls GET https://api.anthropic.com/v1/organizations/usage_report/messages
// Requires an API key with workspace admin access (ANTHROPIC_BILLING_KEY env var).
// See https://docs.anthropic.com/en/api/usage for the current response schema.
public class AnthropicUsageClient(HttpClient http, ILogger<AnthropicUsageClient> logger) : IAnthropicUsageClient
{
    // Requesting more pages than this for a single day's usage indicates the pagination
    // token is not advancing (e.g. an API change) — bail rather than loop unbounded.
    private const int MaxPages = 100;

    // Per-1M token rates (USD). Anthropic usage API returns token counts but not cost.
    // Keys are model-id prefixes; longest match wins (see ComputeCost).
    private static readonly Dictionary<string, (decimal Input, decimal Output, decimal CacheRead, decimal CacheWrite)> Pricing = new()
    {
        ["claude-3-5-sonnet"] = (3.0m, 15.0m, 0.30m, 3.75m),
        ["claude-3-5-haiku"] = (0.8m, 4.0m, 0.08m, 1.00m),
        ["claude-opus-4"] = (15.0m, 75.0m, 1.50m, 18.75m),
        ["claude-sonnet-4"] = (3.0m, 15.0m, 0.30m, 3.75m),
        ["claude-sonnet-5"] = (3.0m, 15.0m, 0.30m, 3.75m),
        ["claude-haiku-4"] = (0.8m, 4.0m, 0.08m, 1.00m),
        ["claude-3-opus"] = (15.0m, 75.0m, 1.50m, 18.75m),
        ["claude-3-sonnet"] = (3.0m, 15.0m, 0.30m, 3.75m),
        ["claude-3-haiku"] = (0.25m, 1.25m, 0.03m, 0.3125m),
    };

    // Sonnet 5 introductory rate, in effect through this date inclusive (Anthropic-announced).
    private static readonly LocalDate Sonnet5IntroPricingEndsOn = new(2026, 8, 31);
    private static readonly (decimal Input, decimal Output, decimal CacheRead, decimal CacheWrite) Sonnet5IntroPricing =
        (2.0m, 10.0m, 0.20m, 2.50m);

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
                LocalDate parsedDate = date;
                if (bucket.StartingAt is { Length: >= 10 } startingAt)
                {
                    var parseResult = LocalDatePattern.Iso.Parse(startingAt[..10]);
                    if (parseResult.Success)
                    {
                        parsedDate = parseResult.Value;
                    }
                }

                // The usage report nests per-model token counts inside each bucket's
                // results[] array; the bucket itself carries only the time window.
                foreach (var r in bucket.Results ?? [])
                {
                    var model = r.Model ?? "unknown";
                    var costUsd = ComputeCost(model, parsedDate, r.InputTokens, r.OutputTokens,
                        r.CacheReadInputTokens, r.CacheCreationInputTokens);

                    allRecords.Add(new AnthropicUsageRecord(
                        Date: parsedDate,
                        Model: model,
                        InputTokens: r.InputTokens,
                        OutputTokens: r.OutputTokens,
                        CacheReadTokens: r.CacheReadInputTokens,
                        CacheWriteTokens: r.CacheCreationInputTokens,
                        CostUsd: costUsd,
                        RawJson: JsonSerializer.Serialize(r, options)));
                }
            }

            hasMore = response?.HasMore == true && !string.IsNullOrEmpty(response.NextPage);
            nextPage = response?.NextPage;
        }

        return allRecords;
    }

    private decimal ComputeCost(string model, LocalDate usageDate, long input, long output, long cacheRead, long cacheWrite)
    {
        // Longest matching prefix wins (same fix as OpenAiUsageClient). The tiers here
        // don't currently share price-differing prefixes, but Contains + FirstOrDefault
        // is the same latent trap, so resolve to the most specific key defensively.
        var match = Pricing
            .Where(kv => model.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(kv => kv.Key.Length)
            .Select(kv => ((decimal, decimal, decimal, decimal)?)kv.Value)
            .FirstOrDefault();

        decimal ir, or, crr, cwr;
        if (match is null)
        {
            // Unknown model — surface it instead of silently mis-costing at Sonnet rates.
            logger.LogWarning("No Anthropic pricing entry for model '{Model}'; defaulting to Sonnet rates. Add an explicit entry to keep cost accurate.", model);
            (ir, or, crr, cwr) = (3.0m, 15.0m, 0.30m, 3.75m);
        }
        else
        {
            (ir, or, crr, cwr) = match.Value;
            // Sonnet 5 launched with introductory pricing through 2026-08-31; usage billed
            // for that window gets the lower rate regardless of when this code runs.
            if (model.StartsWith("claude-sonnet-5", StringComparison.OrdinalIgnoreCase)
                && usageDate <= Sonnet5IntroPricingEndsOn)
            {
                (ir, or, crr, cwr) = Sonnet5IntroPricing;
            }
        }

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
