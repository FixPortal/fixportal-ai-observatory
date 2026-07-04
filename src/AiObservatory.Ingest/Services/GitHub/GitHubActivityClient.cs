using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiObservatory.Data.Repositories;
using Microsoft.Extensions.Logging;
using NodaTime;
using NodaTime.Text;

namespace AiObservatory.Ingest.Services.GitHub;

// Calls the GitHub REST API directly (no out-of-repo hook, unlike Claude Activity).
// Requires a PAT with contents:read, pull-requests:read, actions:read.
public class GitHubActivityClient(HttpClient http, ILogger<GitHubActivityClient> logger) : IGitHubActivityClient
{
    // Stop calling GitHub once headroom drops this low — leaves margin for other
    // callers (e.g. the Copilot client sharing the same token) within the hour window.
    private const int RateLimitFloor = 50;
    private const int PerPage = 100;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public async Task<IReadOnlyList<GitHubPullRequestRecord>> GetPullRequestsAsync(string repo, LocalDate since, CancellationToken ct = default)
    {
        var results = new List<GitHubPullRequestRecord>();
        var page = 1;
        while (true)
        {
            // Default sort for this endpoint is created/desc (newest first) — once a page
            // yields a PR older than `since`, every PR on every later page is older too, so
            // the outer loop can stop instead of paging through a repo's entire PR history
            // on every 3-day poll.
            var response = await http.GetAsync($"/repos/{repo}/pulls?state=all&per_page={PerPage}&page={page}", ct);
            CheckRateLimit(response);
            response.EnsureSuccessStatusCode();
            var prs = await response.Content.ReadFromJsonAsync<List<PullRequestDto>>(JsonOptions, ct) ?? [];

            var reachedOlderThanSince = false;
            foreach (var pr in prs)
            {
                var createdAt = InstantPattern.ExtendedIso.Parse(pr.CreatedAt).Value;
                if (createdAt.InUtc().Date < since)
                {
                    reachedOlderThanSince = true;
                    break;
                }

                var (reviewCount, firstReviewAt) = await GetReviewSummaryAsync(repo, pr.Number, ct);
                results.Add(new GitHubPullRequestRecord(
                    repo, pr.Number, pr.Title, pr.User.Login, pr.State,
                    createdAt,
                    pr.MergedAt is null ? null : InstantPattern.ExtendedIso.Parse(pr.MergedAt).Value,
                    pr.ClosedAt is null ? null : InstantPattern.ExtendedIso.Parse(pr.ClosedAt).Value,
                    firstReviewAt, reviewCount));
            }

            if (reachedOlderThanSince || prs.Count < PerPage) break;
            page++;
        }
        return results;
    }

    private async Task<(int ReviewCount, Instant? FirstReviewAt)> GetReviewSummaryAsync(string repo, int number, CancellationToken ct)
    {
        var response = await http.GetAsync($"/repos/{repo}/pulls/{number}/reviews", ct);
        CheckRateLimit(response);
        response.EnsureSuccessStatusCode();
        var reviews = await response.Content.ReadFromJsonAsync<List<ReviewDto>>(JsonOptions, ct) ?? [];
        if (reviews.Count == 0) return (0, null);

        var first = reviews
            .Select(r => InstantPattern.ExtendedIso.Parse(r.SubmittedAt).Value)
            .Min();
        return (reviews.Count, first);
    }

    private void CheckRateLimit(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("X-RateLimit-Remaining", out var values)
            && int.TryParse(values.FirstOrDefault(), out var remaining)
            && remaining < RateLimitFloor)
        {
            logger.LogWarning("GitHub API rate limit at {Remaining}; aborting remaining repos this poll cycle", remaining);
            throw new GitHubRateLimitExceededException(remaining);
        }
    }

    public Task<IReadOnlyList<GitHubCommitRecord>> GetCommitsAsync(string repo, LocalDate since, CancellationToken ct = default) =>
        throw new NotImplementedException("Added in Task 7");

    public Task<IReadOnlyList<GitHubWorkflowRunRecord>> GetWorkflowRunsAsync(string repo, LocalDate since, CancellationToken ct = default) =>
        throw new NotImplementedException("Added in Task 8");

    private sealed record PullRequestDto(
        int Number, string Title, PullRequestUserDto User, string State,
        string CreatedAt, string? MergedAt, string? ClosedAt);
    private sealed record PullRequestUserDto(string Login);
    private sealed record ReviewDto(string SubmittedAt);
}
