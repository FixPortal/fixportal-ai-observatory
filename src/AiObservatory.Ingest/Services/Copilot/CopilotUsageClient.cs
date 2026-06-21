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
        var response = await http.GetFromJsonAsync<List<CopilotOrgUsageResponse>>(
            $"/orgs/{org}/copilot/metrics?since={dateStr}&until={dateStr}", options, ct);
        if (response is null || response.Count == 0)
        {
            return null;
        }

        var first = response[0];
        return new CopilotUsageRecord(
            Date: date,
            ActiveUsers: first.TotalActiveUsers,
            TotalSuggestionsCount: first.TotalSuggestionsCount,
            TotalAcceptancesCount: first.TotalAcceptancesCount,
            RawJson: JsonSerializer.Serialize(first, options));
    }

    private sealed record CopilotOrgUsageResponse(
        int TotalActiveUsers,
        int TotalEngagedUsers,
        int TotalSuggestionsCount,
        int TotalAcceptancesCount);
}
