using AiObservatory.Data;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace AiObservatory.Api.Endpoints;

public static class GitHubActivityEndpoints
{
    public static void MapGitHubActivityEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/github/prs", async (
            AiObservatoryDbContext db, IClock clock, string? from, string? to, CancellationToken ct) =>
        {
            var today = clock.GetCurrentInstant().InUtc().Date;
            if (!ActivityEndpoints.TryParseDateRange(from, to, today, out var start, out var end, out var error))
            {
                return error!;
            }
            var startInstant = start.AtStartOfDayInZone(DateTimeZone.Utc).ToInstant();
            var endInstant = end.PlusDays(1).AtStartOfDayInZone(DateTimeZone.Utc).ToInstant();

            var prs = await db.GitHubPullRequests
                .AsNoTracking()
                .Where(p => p.CreatedAt >= startInstant && p.CreatedAt < endInstant)
                .OrderByDescending(p => p.CreatedAt)
                .ToListAsync(ct);

            var response = prs.Select(p => new GitHubPrResponse(
                p.Repo, p.Number, p.Title, p.Author, p.State,
                p.CreatedAt.ToString(), p.MergedAt?.ToString(),
                p.ReviewCount, ComputeTurnaroundHours(p.CreatedAt, p.FirstReviewAt)));

            return Results.Ok(response);
        }).AddEndpointFilter<AdminOnlyApiKeyEndpointFilter>();

        app.MapGet("/github/commits/summary", async (
            AiObservatoryDbContext db, IClock clock, string? from, string? to, CancellationToken ct) =>
        {
            var today = clock.GetCurrentInstant().InUtc().Date;
            if (!ActivityEndpoints.TryParseDateRange(from, to, today, out var start, out var end, out var error))
            {
                return error!;
            }
            var startInstant = start.AtStartOfDayInZone(DateTimeZone.Utc).ToInstant();
            var endInstant = end.PlusDays(1).AtStartOfDayInZone(DateTimeZone.Utc).ToInstant();

            var commits = await db.GitHubCommits
                .AsNoTracking()
                .Where(c => c.CommittedAt >= startInstant && c.CommittedAt < endInstant)
                .Select(c => new { c.Repo, c.Additions, c.Deletions })
                .ToListAsync(ct);

            var byRepo = commits
                .GroupBy(c => c.Repo)
                .Select(g => new GitHubCommitSummaryResponse(g.Key, g.Count(), g.Sum(c => c.Additions), g.Sum(c => c.Deletions)))
                .OrderByDescending(r => r.CommitCount)
                .ToList();

            return Results.Ok(byRepo);
        }).AddEndpointFilter<AdminOnlyApiKeyEndpointFilter>();

        app.MapGet("/github/ci", async (
            AiObservatoryDbContext db, IClock clock, string? from, string? to, CancellationToken ct) =>
        {
            var today = clock.GetCurrentInstant().InUtc().Date;
            if (!ActivityEndpoints.TryParseDateRange(from, to, today, out var start, out var end, out var error))
            {
                return error!;
            }
            var startInstant = start.AtStartOfDayInZone(DateTimeZone.Utc).ToInstant();
            var endInstant = end.PlusDays(1).AtStartOfDayInZone(DateTimeZone.Utc).ToInstant();

            var runs = await db.GitHubWorkflowRuns
                .AsNoTracking()
                .Where(r => r.CreatedAt >= startInstant && r.CreatedAt < endInstant)
                .Select(r => new { r.Repo, r.WorkflowName, r.Status })
                .ToListAsync(ct);

            var byRepoWorkflow = runs
                .GroupBy(r => (r.Repo, r.WorkflowName))
                .Select(g =>
                {
                    var total = g.Count();
                    var failed = g.Count(r => r.Status == "failure");
                    return new GitHubCiResponse(
                        g.Key.Repo, g.Key.WorkflowName, total, failed,
                        total > 0 ? Math.Round((total - failed) * 100.0 / total, 1) : 0);
                })
                .OrderByDescending(r => r.TotalRuns)
                .ToList();

            return Results.Ok(byRepoWorkflow);
        }).AddEndpointFilter<AdminOnlyApiKeyEndpointFilter>();
    }

    public static double? ComputeTurnaroundHours(Instant createdAt, Instant? firstReviewAt)
    {
        if (firstReviewAt is not { } reviewedAt) return null;
        return Math.Round((reviewedAt - createdAt).TotalHours, 1);
    }
}

public sealed record GitHubPrResponse(
    string Repo, int Number, string Title, string Author, string State,
    string CreatedAt, string? MergedAt, int ReviewCount, double? TurnaroundHours);

public sealed record GitHubCommitSummaryResponse(string Repo, int CommitCount, int Additions, int Deletions);

public sealed record GitHubCiResponse(string Repo, string WorkflowName, int TotalRuns, int FailedRuns, double SuccessRate);
