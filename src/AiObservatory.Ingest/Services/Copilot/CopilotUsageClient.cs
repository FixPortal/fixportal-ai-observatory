using System.Net.Http.Json;
using System.Text.Json;
using NodaTime;
using NodaTime.Text;

namespace AiObservatory.Ingest.Services.Copilot;

// RETIRED ENDPOINT — this client does not work as written.
//
// It calls GET https://api.github.com/orgs/{org}/copilot/metrics, which GitHub shut down
// on 2 April 2026:
//   https://github.blog/changelog/2026-01-29-closing-down-notice-of-legacy-copilot-metrics-apis/
// Organization metrics now live under /orgs/{org}/copilot/metrics/reports/* :
//   https://docs.github.com/en/rest/copilot/copilot-usage-metrics
// Setting COPILOT_ORG today therefore produces failed requests, not ingestion. The arm
// stays disabled (no `copilot-org` Key Vault secret) and must be retargeted before use.
//
// Requires GITHUB_TOKEN with manage_billing:copilot scope and COPILOT_ORG org name.
// Returns aggregate activity metrics; token-level data is not available via this API —
// use the session-end extension (see docs) for per-session token tracking.
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
