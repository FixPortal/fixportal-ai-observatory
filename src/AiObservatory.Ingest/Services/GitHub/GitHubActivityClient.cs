using System.Net.Http.Json;
using System.Text.Json;
using AiObservatory.Data.Repositories;
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
    // Caps the Actions list-runs endpoint (not the Search API, which this client never calls).
    private const int WorkflowRunsPaginationCap = 1000;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public async Task<IReadOnlyList<GitHubPullRequestRecord>> GetPullRequestsAsync(string repo, LocalDate since, CancellationToken ct = default)
    {
        var results = new List<GitHubPullRequestRecord>();
        var page = 1;
        while (true)
        {
            // Sorted by updated/desc (not created/desc) so a PR opened long ago that gets
            // merged/reviewed/commented on THIS week still surfaces near the top — otherwise
            // its stale State/MergedAt/ReviewCount would never be re-fetched once it fell past
            // the early-break point on created_at. Once a page yields a PR whose updated_at
            // predates `since`, every PR on every later page is even less recently updated, so
            // the outer loop can stop instead of paging through a repo's entire PR history on
            // every 3-day poll.
            using var response = await http.GetAsync($"/repos/{repo}/pulls?state=all&sort=updated&direction=desc&per_page={PerPage}&page={page}", ct);
            CheckRateLimit(response);
            response.EnsureSuccessStatusCode();
            var prs = await response.Content.ReadFromJsonAsync<List<PullRequestDto>>(JsonOptions, ct) ?? [];

            var reachedOlderThanSince = false;
            foreach (var pr in prs)
            {
                var updatedAt = InstantPattern.ExtendedIso.Parse(pr.UpdatedAt).Value;
                if (updatedAt.InUtc().Date < since)
                {
                    reachedOlderThanSince = true;
                    break;
                }

                results.Add(await CreatePullRequestRecordAsync(repo, pr, ct));
            }

            if (reachedOlderThanSince || prs.Count < PerPage)
            {
                break;
            }
            page++;
        }
        return results;
    }

    private async Task<GitHubPullRequestRecord> CreatePullRequestRecordAsync(
        string repo,
        PullRequestDto pullRequest,
        CancellationToken ct)
    {
        var createdAt = InstantPattern.ExtendedIso.Parse(pullRequest.CreatedAt).Value;
        var (reviewCount, firstReviewAt) = await GetReviewSummaryAsync(repo, pullRequest.Number, ct);
        Instant? mergedAt = pullRequest.MergedAt is null
            ? null
            : InstantPattern.ExtendedIso.Parse(pullRequest.MergedAt).Value;

        // GitHub returns only open/closed in state; mergedness is carried separately.
        var state = mergedAt is not null ? "merged" : pullRequest.State;
        return new GitHubPullRequestRecord(
            Truncate(repo, 200),
            pullRequest.Number,
            Truncate(pullRequest.Title, 500),
            Truncate(pullRequest.User.Login, 200),
            Truncate(state, 20),
            createdAt,
            mergedAt,
            pullRequest.ClosedAt is null ? null : InstantPattern.ExtendedIso.Parse(pullRequest.ClosedAt).Value,
            firstReviewAt,
            reviewCount);
    }

    private async Task<(int ReviewCount, Instant? FirstReviewAt)> GetReviewSummaryAsync(string repo, int number, CancellationToken ct)
    {
        var reviews = new List<ReviewDto>();
        var page = 1;
        while (true)
        {
            using var response = await http.GetAsync($"/repos/{repo}/pulls/{number}/reviews?per_page={PerPage}&page={page}", ct);
            CheckRateLimit(response);
            response.EnsureSuccessStatusCode();
            var pageReviews = await response.Content.ReadFromJsonAsync<List<ReviewDto>>(JsonOptions, ct) ?? [];
            reviews.AddRange(pageReviews);
            if (pageReviews.Count < PerPage)
            {
                break;
            }
            page++;
        }

        if (reviews.Count == 0)
        {
            return (0, null);
        }

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
            using var response = await http.GetAsync($"/repos/{repo}/commits?since={sinceStr}&per_page={PerPage}&page={page}", ct);
            CheckRateLimit(response);
            response.EnsureSuccessStatusCode();
            var commits = await response.Content.ReadFromJsonAsync<List<CommitListDto>>(JsonOptions, ct) ?? [];

            foreach (var c in commits)
            {
                // Per-commit call needed for churn stats — the list endpoint omits them.
                // Personal-scale repo volume keeps this within the rate-limit budget.
                using var detailResponse = await http.GetAsync($"/repos/{repo}/commits/{c.Sha}", ct);
                CheckRateLimit(detailResponse);
                detailResponse.EnsureSuccessStatusCode();
                var detail = await detailResponse.Content.ReadFromJsonAsync<CommitDetailDto>(JsonOptions, ct)
                    ?? new CommitDetailDto(c.Sha, new CommitStatsDto(0, 0));

                results.Add(new GitHubCommitRecord(
                    Truncate(repo, 200), Truncate(c.Sha, 64), Truncate(c.Commit.Author.Name, 200),
                    InstantPattern.ExtendedIso.Parse(c.Commit.Author.Date).Value,
                    detail.Stats.Additions, detail.Stats.Deletions));
            }

            if (commits.Count < PerPage)
            {
                break;
            }
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
            using var response = await http.GetAsync($"/repos/{repo}/actions/runs?created=%3E%3D{sinceStr}&per_page={PerPage}&page={page}", ct);
            CheckRateLimit(response);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync<WorkflowRunsResponseDto>(JsonOptions, ct)
                ?? new WorkflowRunsResponseDto([]);

            foreach (var run in body.WorkflowRuns)
            {
                results.Add(new GitHubWorkflowRunRecord(
                    Truncate(repo, 200), run.Id, Truncate(run.Name ?? "(unnamed)", 200), Truncate(run.Conclusion ?? run.Status, 20),
                    InstantPattern.ExtendedIso.Parse(run.CreatedAt).Value));
            }

            if (body.WorkflowRuns.Count < PerPage)
            {
                break;
            }
            if (page * PerPage >= WorkflowRunsPaginationCap)
            {
                logger.LogWarning("GitHub workflow-runs result cap reached for {Repo}; narrowing the backfill window may be required", repo);
                break;
            }
            page++;
        }
        return results;
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private sealed record WorkflowRunsResponseDto(List<WorkflowRunDto> WorkflowRuns);
    private sealed record WorkflowRunDto(long Id, string? Name, string Status, string? Conclusion, string CreatedAt);

    private sealed record PullRequestDto(
        int Number, string Title, PullRequestUserDto User, string State,
        string CreatedAt, string UpdatedAt, string? MergedAt, string? ClosedAt);
    private sealed record PullRequestUserDto(string Login);
    private sealed record ReviewDto(string? SubmittedAt);
    private sealed record CommitListDto(string Sha, CommitInnerDto Commit);
    private sealed record CommitInnerDto(CommitAuthorDto Author);
    private sealed record CommitAuthorDto(string Name, string Date);
    private sealed record CommitDetailDto(string Sha, CommitStatsDto Stats);
    private sealed record CommitStatsDto(int Additions, int Deletions);
}
