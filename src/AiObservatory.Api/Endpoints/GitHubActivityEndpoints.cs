using System.Linq.Expressions;
using AiObservatory.Data;
using AiObservatory.Data.Entities;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace AiObservatory.Api.Endpoints;

// Response record properties are consumed by ASP.NET Core JSON serialization.
// ReSharper disable NotAccessedPositionalProperty.Global

public static class GitHubActivityEndpoints
{
    private static readonly string[] TerminalFailureStatuses = ["failure", "timed_out", "startup_failure"];

    // Owners lowercased once here, and the column lowercased in SQL below, because this
    // comparison MUST be case-insensitive — unlike ActivityEndpoints' ordinal one.
    //
    // Two different domains. A Claude session's Project comes from a folder path, where
    // case is meaningful and ordinal is correct. A GitHub owner/repo is case-insensitive by
    // definition, and GitHubIngestionService deliberately normalises it with
    // ToLowerInvariant before writing, so every stored Repo is lowercase while
    // AllowedProjectOwners carries the display casing "FixPortal". Comparing those
    // ordinally matched nothing: "fixportal/x".StartsWith("FixPortal/") is false.
    //
    // That filtered out EVERY ingested GitHub row. It stayed invisible because the ingest
    // worker had never once started in Azure (it failed App Service's startup probe), so
    // the read path had nothing to drop — and the only test seeded "FixPortal/..." by hand,
    // encoding the filter's assumption rather than the producer's actual output.
    private static readonly string[] AllowedRepoOwners =
        [.. ActivityEndpoints.AllowedProjectOwners.Select(o => o.ToLowerInvariant())];

    // Same allowlist rule as ActivityEndpoints.IsAllowedProjectPredicate, but PRs/
    // commits/CI runs are three unrelated entity types (no shared interface) that each
    // expose a plain string Repo — so the one shared predicate body is spliced onto
    // each entity's own Repo access via IsAllowedRepo<T> rather than duplicated per query.
    private static readonly Expression<Func<string, bool>> RepoAllowedTemplate =
        repo => AllowedRepoOwners.Any(o => repo.ToLower() == o || repo.ToLower().StartsWith(o + "/"));

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

            // Projecting straight into the GitHubCommitSummaryResponse record inside the
            // GroupBy/Select (as the equivalent PR/CI queries do into an anonymous type)
            // fails to translate here — EF Core cannot turn a record constructor call
            // carrying three separate group aggregates (Count + two Sums) into SQL when
            // combined with the correlated EXISTS subquery IsAllowedRepo compiles to, and
            // throws InvalidOperationException at request time instead of at startup. The
            // /github/ci query below sidesteps the same trap by materializing into an
            // anonymous type first and mapping to its response record afterward; mirror
            // that here.
            var grouped = await db.GitHubCommits
                .AsNoTracking()
                .Where(IsAllowedRepo<GitHubCommit>(c => c.Repo))
                .Where(c => c.CommittedAt >= startInstant && c.CommittedAt < endInstant)
                .GroupBy(c => c.Repo)
                .Select(g => new
                {
                    Repo = g.Key,
                    CommitCount = g.Count(),
                    Additions = g.Sum(c => c.Additions),
                    Deletions = g.Sum(c => c.Deletions),
                })
                .OrderByDescending(r => r.CommitCount)
                .ToListAsync(ct);

            var byRepo = grouped
                .Select(r => new GitHubCommitSummaryResponse(r.Repo, r.CommitCount, r.Additions, r.Deletions))
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
                    Failed = g.Count(r => Enumerable.Contains(TerminalFailureStatuses, r.Status)),
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
