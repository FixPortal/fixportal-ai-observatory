using System.Net.Http.Json;
using System.Text.Json;
using NodaTime;

// ReSharper disable NotAccessedPositionalProperty.Local; unused fields kept for shape-fidelity with the API response.

namespace AiObservatory.Ingest.Services.OpenAi;

// Calls GET https://api.openai.com/v1/organization/usage/completions
// Requires an admin API key (OPENAI_ADMIN_KEY env var) with the
// openai.usage.read permission (create one at platform.openai.com/api-keys).
// See https://developers.openai.com/api/reference/resources/organization/subresources/usage/methods/completions
// for the schema.
public class OpenAiUsageClient(HttpClient http, ILogger<OpenAiUsageClient> logger) : IOpenAiUsageClient
{
    // Requesting more pages than this for a single day's usage indicates the pagination
    // token is not advancing (e.g. an API change) — bail rather than loop unbounded.
    // Mirrors AnthropicUsageClient.MaxPages.
    private const int MaxPages = 100;

    public async Task<IReadOnlyList<OpenAiUsageRecord>> GetDailyUsageAsync(
        LocalDate date,
        CancellationToken ct = default
    )
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
                logger.LogWarning(
                    "OpenAI usage pagination exceeded {MaxPages} pages for {Date}; stopping",
                    MaxPages,
                    date
                );
                break;
            }

            var url =
                $"/v1/organization/usage/completions?start_time={startTime}&end_time={endTime}&bucket_width=1d&group_by[]=model&limit=100";
            if (!string.IsNullOrEmpty(nextPage))
            {
                url += $"&page={nextPage}";
            }

            var response = await http.GetFromJsonAsync<OpenAiUsageApiResponse>(url, options, ct);

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
        OpenAiUsageBucket bucket,
        LocalDate date,
        JsonSerializerOptions options,
        ICollection<OpenAiUsageRecord> records
    )
    {
        foreach (var result in bucket.Results ?? [])
        {
            if (string.IsNullOrEmpty(result.Model))
            {
                continue;
            }
            if (result.InputUncachedTokens is null || result.OutputTokens is null)
            {
                throw new JsonException("OpenAI usage result is missing input_uncached_tokens or output_tokens.");
            }

            records.Add(
                new OpenAiUsageRecord(
                    Date: date,
                    Model: result.Model,
                    InputTokens: result.InputUncachedTokens.Value,
                    OutputTokens: result.OutputTokens.Value,
                    CachedInputTokens: result.InputCachedTokens,
                    CacheWriteTokens: result.InputCacheWriteTokens,
                    RawJson: JsonSerializer.Serialize(result, options)
                )
            );
        }
    }

    private sealed record OpenAiUsageApiResponse(List<OpenAiUsageBucket>? Data, bool? HasMore, string? NextPage);

    private sealed record OpenAiUsageBucket(long StartTime, long EndTime, List<OpenAiUsageResult>? Results);

    private sealed record OpenAiUsageResult(
        string? Model,
        long? InputTokens,
        long? OutputTokens,
        long InputCachedTokens,
        long InputCacheWriteTokens,
        long? InputUncachedTokens,
        long NumModelRequests
    );
}
