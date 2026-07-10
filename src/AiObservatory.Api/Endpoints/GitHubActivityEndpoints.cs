using System.Linq.Expressions;
using AiObservatory.Data;
using AiObservatory.Data.Entities;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace AiObservatory.Api.Endpoints;

public static class GitHubActivityEndpoints
{
    private static readonly string[] TerminalFailureStatuses = ["failure", "timed_out", "startup_failure"];

    // Same allowlist rule as ActivityEndpoints.IsAllowedProjectPredicate, but PRs/
    // commits/CI runs are three unrelated entity types (no shared interface) that each
    // expose a plain string Repo — so the one shared predicate body is spliced onto
    // each entity's own Repo access via IsAllowedRepo<T> rather than duplicated per query.
    private static readonly Expression<Func<string, bool>> RepoAllowedTemplate =
        repo => ActivityEndpoints.AllowedProjectOwners.Any(o => repo == o || repo.StartsWith(o + "/"));

    private static Expression<Func<T, bool>> IsAllowedRepo<T>(Expression<Func<T, string>> repoSelector)
    {
        var body = new ReplaceParameterVisitor(RepoAllowedTemplate.Parameters[0], repoSelector.Body)
            .Visit(RepoAllowedTemplate.Body)!;
        return Expression.Lambda<Func<T, bool>>(body, repoSelector.Parameters[0]);
    }

    private sealed class ReplaceParameterVisitor(ParameterExpression from, Expression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : node;
    }

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
                .Where(IsAllowedRepo<GitHubPullRequest>(p => p.Repo))
                .Where(p =>
                    p.CreatedAt >= startInstant && p.CreatedAt < endInstant ||
                    p.MergedAt != null && p.MergedAt >= startInstant && p.MergedAt < endInstant ||
                    p.FirstReviewAt != null && p.FirstReviewAt >= startInstant && p.FirstReviewAt < endInstant)
                .OrderByDescending(p => p.CreatedAt)
                .ToListAsync(ct);

            var response = prs.Select(p => new GitHubPrResponse(
                p.Repo, p.Number, p.Title, p.Author, p.State,
                p.CreatedAt, p.MergedAt,
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

            var byRepo = await db.GitHubCommits
                .AsNoTracking()
                .Where(IsAllowedRepo<GitHubCommit>(c => c.Repo))
                .Where(c => c.CommittedAt >= startInstant && c.CommittedAt < endInstant)
                .GroupBy(c => c.Repo)
                .Select(g => new GitHubCommitSummaryResponse(g.Key, g.Count(), g.Sum(c => c.Additions), g.Sum(c => c.Deletions)))
                .OrderByDescending(r => r.CommitCount)
                .ToListAsync(ct);

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

            var grouped = await db.GitHubWorkflowRuns
                .AsNoTracking()
                .Where(IsAllowedRepo<GitHubWorkflowRun>(r => r.Repo))
                .Where(r => r.CreatedAt >= startInstant && r.CreatedAt < endInstant)
                .GroupBy(r => new { r.Repo, r.WorkflowName })
                .Select(g => new
                {
                    g.Key.Repo,
                    g.Key.WorkflowName,
                    Total = g.Count(),
                    Failed = g.Count(r => TerminalFailureStatuses.Contains(r.Status)),
                    Succeeded = g.Count(r => r.Status == "success"),
                })
                .OrderByDescending(r => r.Total)
                .ToListAsync(ct);

            var byRepoWorkflow = grouped
                .Select(r => new GitHubCiResponse(r.Repo, r.WorkflowName, r.Total, r.Failed, ComputeSuccessRate(r.Total, r.Succeeded)))
                .ToList();

            return Results.Ok(byRepoWorkflow);
        }).AddEndpointFilter<AdminOnlyApiKeyEndpointFilter>();
    }

    public static double? ComputeTurnaroundHours(Instant createdAt, Instant? firstReviewAt)
    {
        if (firstReviewAt is not { } reviewedAt)
        {
            return null;
        }
        return Math.Round((reviewedAt - createdAt).TotalHours, 1);
    }

    // Only runs with Status == "success" count toward the rate — cancelled/in_progress/queued
    // runs count toward the total but are neither a success nor a (terminal) failure.
    public static double ComputeSuccessRate(int total, int succeeded) =>
        total > 0 ? Math.Round(succeeded * 100.0 / total, 1) : 0;

    public static bool IsTerminalFailure(string status) =>
        TerminalFailureStatuses.Contains(status);
}

public sealed record GitHubPrResponse(
    string Repo, int Number, string Title, string Author, string State,
    Instant CreatedAt, Instant? MergedAt, int ReviewCount, double? TurnaroundHours);

public sealed record GitHubCommitSummaryResponse(string Repo, int CommitCount, int Additions, int Deletions);

public sealed record GitHubCiResponse(string Repo, string WorkflowName, int TotalRuns, int FailedRuns, double SuccessRate);
