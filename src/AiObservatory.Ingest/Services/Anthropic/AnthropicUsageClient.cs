using System.Net.Http.Json;
using System.Text.Json;
using NodaTime;
using NodaTime.Text;

// ReSharper disable NotAccessedPositionalProperty.Local; unused fields kept for shape-fidelity with the API response.

namespace AiObservatory.Ingest.Services.Anthropic;

// Calls GET https://api.anthropic.com/v1/organizations/usage_report/messages
// Requires an API key with workspace admin access (ANTHROPIC_BILLING_KEY env var).
// See https://docs.anthropic.com/en/api/usage for the current response schema.
public class AnthropicUsageClient(HttpClient http, ILogger<AnthropicUsageClient> logger) : IAnthropicUsageClient
{
    // Requesting more pages than this for a single day's usage indicates the pagination
    // token is not advancing (e.g. an API change) — bail rather than loop unbounded.
    private const int MaxPages = 100;

    public async Task<IReadOnlyList<AnthropicUsageRecord>> GetUsageAsync(LocalDate date, CancellationToken ct = default)
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
                logger.LogWarning(
                    "Anthropic usage pagination exceeded {MaxPages} pages for {Date}; stopping",
                    MaxPages,
                    date
                );
                break;
            }

            var url =
                $"/v1/organizations/usage_report/messages?starting_at={startStr}&ending_at={endStr}&bucket_width=1d&group_by[]=model";
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

    private static void AddBucketRecords(
        AnthropicUsageBucket bucket,
        LocalDate fallbackDate,
        JsonSerializerOptions options,
        ICollection<AnthropicUsageRecord> records
    )
    {
        var date = ParseBucketDate(bucket.StartingAt, fallbackDate);

        // The usage report nests per-model token counts inside each bucket's
        // results[] array; the bucket itself carries only the time window.
        foreach (var result in bucket.Results ?? [])
        {
            if (result.InputTokens is null || result.OutputTokens is null)
            {
                throw new JsonException("Anthropic usage result is missing input_tokens or output_tokens.");
            }

            var model = result.Model ?? "unknown";
            records.Add(
                new AnthropicUsageRecord(
                    Date: date,
                    Model: model,
                    InputTokens: result.InputTokens.Value,
                    OutputTokens: result.OutputTokens.Value,
                    CacheReadTokens: result.CacheReadInputTokens,
                    CacheWriteTokens: result.CacheCreationInputTokens,
                    RawJson: JsonSerializer.Serialize(result, options)
                )
            );
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

    private sealed record AnthropicUsageApiResponse(List<AnthropicUsageBucket>? Data, bool? HasMore, string? NextPage);

    private sealed record AnthropicUsageBucket(
        string? StartingAt,
        string? EndingAt,
        List<AnthropicUsageResult>? Results
    );

    private sealed record AnthropicUsageResult(
        string? Model,
        long? InputTokens,
        long? OutputTokens,
        long CacheReadInputTokens,
        long CacheCreationInputTokens
    );
}
