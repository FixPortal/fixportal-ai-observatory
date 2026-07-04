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
                Instant? mergedAt = pr.MergedAt is null ? null : InstantPattern.ExtendedIso.Parse(pr.MergedAt).Value;
                // GitHub's REST API only ever returns "open"/"closed" for `state` — mergedness
                // is signaled separately via `merged_at`. Derive the 3-way state promised by
                // the entity/frontend rather than passing the raw 2-way API value through.
                var state = mergedAt is not null ? "merged" : pr.State;
                results.Add(new GitHubPullRequestRecord(
                    repo, pr.Number, pr.Title, pr.User.Login, state,
                    createdAt,
                    mergedAt,
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

        // Pending reviews (not yet submitted) omit submitted_at entirely — only submitted
        // reviews count toward FirstReviewAt, but ReviewCount still reflects every review.
        var submittedAts = reviews
            .Where(r => r.SubmittedAt is not null)
            .Select(r => InstantPattern.ExtendedIso.Parse(r.SubmittedAt!).Value)
            .ToList();
        Instant? first = submittedAts.Count > 0 ? submittedAts.Min() : null;
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

    public async Task<IReadOnlyList<GitHubCommitRecord>> GetCommitsAsync(string repo, LocalDate since, CancellationToken ct = default)
    {
        // GitHub's `since` param on this endpoint requires a full timestamp, not a bare date —
        // a bare date is silently ignored by the API and would return the repo's entire history.
        var sinceStr = InstantPattern.ExtendedIso.Format(since.AtStartOfDayInZone(DateTimeZone.Utc).ToInstant());
        var results = new List<GitHubCommitRecord>();
        var page = 1;
        while (true)
        {
            var response = await http.GetAsync($"/repos/{repo}/commits?since={sinceStr}&per_page={PerPage}&page={page}", ct);
            CheckRateLimit(response);
            response.EnsureSuccessStatusCode();
            var commits = await response.Content.ReadFromJsonAsync<List<CommitListDto>>(JsonOptions, ct) ?? [];

            foreach (var c in commits)
            {
                // Per-commit call needed for churn stats — the list endpoint omits them.
                // Personal-scale repo volume keeps this within the rate-limit budget.
                var detailResponse = await http.GetAsync($"/repos/{repo}/commits/{c.Sha}", ct);
                CheckRateLimit(detailResponse);
                detailResponse.EnsureSuccessStatusCode();
                var detail = await detailResponse.Content.ReadFromJsonAsync<CommitDetailDto>(JsonOptions, ct)
                    ?? new CommitDetailDto(c.Sha, new CommitStatsDto(0, 0));

                results.Add(new GitHubCommitRecord(
                    repo, c.Sha, c.Commit.Author.Name,
                    InstantPattern.ExtendedIso.Parse(c.Commit.Author.Date).Value,
                    detail.Stats.Additions, detail.Stats.Deletions));
            }

            if (commits.Count < PerPage) break;
            page++;
        }
        return results;
    }

    public async Task<IReadOnlyList<GitHubWorkflowRunRecord>> GetWorkflowRunsAsync(string repo, LocalDate since, CancellationToken ct = default)
    {
        var sinceStr = LocalDatePattern.Iso.Format(since);
        var results = new List<GitHubWorkflowRunRecord>();
        var page = 1;
        while (true)
        {
            var response = await http.GetAsync($"/repos/{repo}/actions/runs?created=%3E%3D{sinceStr}&per_page={PerPage}&page={page}", ct);
            CheckRateLimit(response);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync<WorkflowRunsResponseDto>(JsonOptions, ct)
                ?? new WorkflowRunsResponseDto([]);

            foreach (var run in body.WorkflowRuns)
            {
                results.Add(new GitHubWorkflowRunRecord(
                    repo, run.Id, run.Name, run.Conclusion ?? run.Status,
                    InstantPattern.ExtendedIso.Parse(run.CreatedAt).Value));
            }

            if (body.WorkflowRuns.Count < PerPage) break;
            page++;
        }
        return results;
    }

    private sealed record WorkflowRunsResponseDto(List<WorkflowRunDto> WorkflowRuns);
    private sealed record WorkflowRunDto(long Id, string Name, string Status, string? Conclusion, string CreatedAt);

    private sealed record PullRequestDto(
        int Number, string Title, PullRequestUserDto User, string State,
        string CreatedAt, string? MergedAt, string? ClosedAt);
    private sealed record PullRequestUserDto(string Login);
    private sealed record ReviewDto(string? SubmittedAt);
    private sealed record CommitListDto(string Sha, CommitInnerDto Commit);
    private sealed record CommitInnerDto(CommitAuthorDto Author);
    private sealed record CommitAuthorDto(string Name, string Date);
    private sealed record CommitDetailDto(string Sha, CommitStatsDto Stats);
    private sealed record CommitStatsDto(int Additions, int Deletions);
}
