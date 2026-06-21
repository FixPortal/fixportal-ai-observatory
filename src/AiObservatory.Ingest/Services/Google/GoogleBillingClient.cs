using System.Net.Http.Json;
using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using NodaTime;
using NodaTime.Text;

namespace AiObservatory.Ingest.Services.Google;

// Calls the Google Cloud Billing API to retrieve daily AI spend.
//
// SETUP REQUIRED:
//   1. Create a service account in Google Cloud Console with the
//      roles/billing.viewer IAM role on your billing account.
//   2. Download the service account JSON key.
//   3. Set GOOGLE_APPLICATION_CREDENTIALS to the path of that JSON key.
//   4. Set GOOGLE_BILLING_ACCOUNT_ID to your billing account ID
//      (format: billingAccounts/XXXXXX-XXXXXX-XXXXXX).
//
// Note: The Cloud Billing API reports aggregate spend by SKU, not per-token usage.
// For per-token tracking use the Stop-hook / session-end extension approach instead.
//
// See https://cloud.google.com/billing/docs/reference/rest for the current API reference.
public class GoogleBillingClient(HttpClient http, string billingAccountId) : IGoogleBillingClient
{
    public async Task<IReadOnlyList<GoogleBillingRecord>> GetDailySpendAsync(
        LocalDate date, CancellationToken ct = default)
    {
        var dateStr = LocalDatePattern.Iso.Format(date);
        
        var credential = await GoogleCredential.GetApplicationDefaultAsync(ct);
        if (credential.IsCreateScopedRequired)
        {
            credential = credential.CreateScoped("https://www.googleapis.com/auth/cloud-platform");
        }
        var token = await credential.UnderlyingCredential.GetAccessTokenForRequestAsync(cancellationToken: ct);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/{billingAccountId}/reports?startDate={dateStr}&endDate={dateStr}");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        using var responseMessage = await http.SendAsync(request, ct);
        responseMessage.EnsureSuccessStatusCode();

        var response = await responseMessage.Content.ReadFromJsonAsync<GoogleBillingApiResponse>(cancellationToken: ct);

        return response?.CostData?.Select(d => new GoogleBillingRecord(
            ServiceDescription: d.ServiceId ?? "unknown",
            Model: MapServiceToModel(d.ServiceId),
            CostUsd: d.Cost,
            RawJson: JsonSerializer.Serialize(d)
        )).ToList() ?? [];
    }

    private static string MapServiceToModel(string? serviceId) => serviceId switch
    {
        string s when s.Contains("gemini-2.5-pro", StringComparison.OrdinalIgnoreCase) => "gemini-2.5-pro",
        string s when s.Contains("gemini-2.5-flash", StringComparison.OrdinalIgnoreCase) => "gemini-2.5-flash",
        string s when s.Contains("gemini-2.0-flash", StringComparison.OrdinalIgnoreCase) => "gemini-2.0-flash",
        string s when s.Contains("gemini-1.5-pro", StringComparison.OrdinalIgnoreCase) => "gemini-1.5-pro",
        string s when s.Contains("gemini-1.5-flash", StringComparison.OrdinalIgnoreCase) => "gemini-1.5-flash",
        _ => serviceId ?? "unknown"
    };

    private sealed record GoogleBillingApiResponse(List<GoogleCostEntry>? CostData);
    private sealed record GoogleCostEntry(string? ServiceId, decimal Cost);
}
