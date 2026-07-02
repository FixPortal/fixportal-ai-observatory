using System.Net.Http.Json;
using System.Text.Json;
using NodaTime;
using NodaTime.Text;

namespace AiObservatory.Ingest.Services.Copilot;

// Calls GET https://api.github.com/orgs/{org}/copilot/metrics?since=YYYY-MM-DD&until=YYYY-MM-DD
// Requires GITHUB_TOKEN with manage_billing:copilot scope and COPILOT_ORG org name.
// Returns aggregate activity metrics; token-level data is not available via this API —
// use the session-end extension (see docs) for per-session token tracking.
// See https://docs.github.com/en/rest/copilot/copilot-usage for the current response schema.
public class CopilotUsageClient(HttpClient http, string org) : ICopilotUsageClient
{
    public async Task<CopilotUsageRecord?> GetDailyUsageAsync(LocalDate date, CancellationToken ct = default)
    {
        var dateStr = LocalDatePattern.Iso.Format(date);
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        // Capture the raw element so RawJson preserves the true API payload (the typed DTO
        // covers only a handful of the metrics response's fields; re-serializing it would
        // drop everything else and record zeros for fields this endpoint no longer returns).
        var response = await http.GetFromJsonAsync<List<JsonElement>>(
            $"/orgs/{org}/copilot/metrics?since={dateStr}&until={dateStr}", options, ct);
        if (response is null || response.Count == 0)
        {
            return null;
        }

        var firstElement = response[0];
        var first = firstElement.Deserialize<CopilotOrgUsageResponse>(options)
            ?? new CopilotOrgUsageResponse(0, 0, 0, 0);
        return new CopilotUsageRecord(
            Date: date,
            ActiveUsers: first.TotalActiveUsers,
            TotalSuggestionsCount: first.TotalSuggestionsCount,
            TotalAcceptancesCount: first.TotalAcceptancesCount,
            RawJson: firstElement.GetRawText());
    }

    private sealed record CopilotOrgUsageResponse(
        int TotalActiveUsers,
        int TotalEngagedUsers,
        int TotalSuggestionsCount,
        int TotalAcceptancesCount);
}
