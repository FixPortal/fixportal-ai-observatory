using System.Globalization;
using System.Net;
using System.Net.Http.Json;

namespace AiObservatory.Api.Services.GitHub;

/// <summary>
/// Reads an organisation's billed usage from GitHub's enhanced billing platform:
/// <c>GET /organizations/{org}/settings/billing/usage</c>.
/// <para>
/// This is the only authoritative source for GitHub spend. The org bill is not paid from
/// the account the spend CSV exports, so before this existed the ledger understated GitHub
/// by the entire org bill — every penny of Actions, Advanced Security and Code Quality AI
/// Credits was invisible.
/// </para>
/// <para>
/// The token needs read access to the org's billing ("Plan" read on a fine-grained PAT, or
/// <c>admin:org</c> on a classic one). That is a different scope from the
/// <c>contents/pull-requests/actions</c> set the activity ingester uses, so the same
/// <c>GITHUB_TOKEN</c> secret must carry both for this arm to work.
/// </para>
/// </summary>
public class GitHubBillingClient(HttpClient http, string org, ILogger<GitHubBillingClient> logger)
{
    /// <summary>
    /// Named-client key. The org is a constructor argument, which a typed client cannot
    /// supply, so the HttpClient is configured by name and the instance is built by a
    /// factory registration.
    /// </summary>
    public const string HttpClientName = "github-billing";

    /// <summary>
    /// Usage for one calendar year, or an empty list when the year predates the org's
    /// billing history or the token cannot see it.
    /// </summary>
    /// <remarks>
    /// Returns empty rather than throwing on 403/404 so a token missing the billing scope
    /// degrades to "no GitHub spend recorded" — a visible gap — instead of failing the
    /// worker's whole daily cycle and taking the other background steps down with it.
    /// </remarks>
    public virtual async Task<IReadOnlyList<GitHubBillingUsageItem>> GetUsageAsync(
        int year, CancellationToken ct = default)
    {
        var url = $"/organizations/{Uri.EscapeDataString(org)}/settings/billing/usage" +
                  $"?year={year.ToString(CultureInfo.InvariantCulture)}";

        using var response = await http.GetAsync(url, ct);

        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            // 403 = token lacks the billing scope; 404 = org invisible to this token, which
            // GitHub also returns for "exists but you may not know that". Same remedy either
            // way, and neither is retryable, so log once per cycle and move on.
            logger.LogWarning(
                "GitHub billing usage unavailable for {Org} ({Status}) — token likely lacks billing read scope",
                org, (int)response.StatusCode);
            return [];
        }

        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<GitHubBillingUsageResponse>(ct);
        return payload?.UsageItems ?? [];
    }

    private sealed record GitHubBillingUsageResponse(List<GitHubBillingUsageItem>? UsageItems);
}

/// <param name="Date">
/// Start of the usage month, e.g. <c>2026-07-01T00:00:00Z</c>. GitHub reports this platform's
/// usage monthly, so this is a period marker, not the date of an individual charge.
/// </param>
/// <param name="Product">Coarse billing product, e.g. <c>actions</c>, <c>ghas</c>, <c>code_quality</c>.</param>
/// <param name="Sku">Human-readable line, e.g. <c>Code Quality AI Credits</c>.</param>
/// <param name="NetAmount">
/// USD actually billed, after <c>discountAmount</c> is taken off <c>grossAmount</c>. This is
/// the only one of the three that belongs in the ledger — gross would double-count the
/// included-minutes allowance that GitHub gives back on the same invoice.
/// </param>
public sealed record GitHubBillingUsageItem(
    DateTimeOffset Date,
    string Product,
    string Sku,
    decimal NetAmount);
