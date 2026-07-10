using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace AiObservatory.Data.Repositories;

public class GitHubActivityRepository(AiObservatoryDbContext ctx) : IGitHubActivityRepository
{
    public Task UpsertPullRequestAsync(GitHubPullRequestRecord r, Instant ingestedAt, CancellationToken ct = default) =>
        ctx.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "GitHubPullRequests"
                ("Id", "Repo", "Number", "Title", "Author", "State", "CreatedAt", "MergedAt", "ClosedAt", "FirstReviewAt", "ReviewCount", "IngestedAt")
            VALUES
                ({Guid.NewGuid()}, {Truncate(r.Repo, 200)}, {r.Number}, {Truncate(r.Title, 500)}, {Truncate(r.Author, 200)}, {Truncate(r.State, 20)}, {r.CreatedAt}, {r.MergedAt}, {r.ClosedAt}, {r.FirstReviewAt}, {r.ReviewCount}, {ingestedAt})
            ON CONFLICT ("Repo", "Number") DO UPDATE SET
                "Title" = EXCLUDED."Title",
                "State" = EXCLUDED."State",
                "MergedAt" = EXCLUDED."MergedAt",
                "ClosedAt" = EXCLUDED."ClosedAt",
                "FirstReviewAt" = COALESCE(EXCLUDED."FirstReviewAt", "GitHubPullRequests"."FirstReviewAt"),
                "ReviewCount" = GREATEST(EXCLUDED."ReviewCount", "GitHubPullRequests"."ReviewCount")
            """, ct);

    public Task UpsertCommitAsync(GitHubCommitRecord r, Instant ingestedAt, CancellationToken ct = default) =>
        ctx.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "GitHubCommits"
                ("Id", "Repo", "Sha", "Author", "CommittedAt", "Additions", "Deletions", "IngestedAt")
            VALUES
                ({Guid.NewGuid()}, {Truncate(r.Repo, 200)}, {Truncate(r.Sha, 64)}, {Truncate(r.Author, 200)}, {r.CommittedAt}, {r.Additions}, {r.Deletions}, {ingestedAt})
            ON CONFLICT ("Repo", "Sha") DO NOTHING
            """, ct);

    public Task UpsertWorkflowRunAsync(GitHubWorkflowRunRecord r, Instant ingestedAt, CancellationToken ct = default) =>
        ctx.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "GitHubWorkflowRuns"
                ("Id", "Repo", "RunId", "WorkflowName", "Status", "CreatedAt", "IngestedAt")
            VALUES
                ({Guid.NewGuid()}, {Truncate(r.Repo, 200)}, {r.RunId}, {Truncate(r.WorkflowName, 200)}, {Truncate(r.Status, 20)}, {r.CreatedAt}, {ingestedAt})
            ON CONFLICT ("Repo", "RunId") DO UPDATE SET
                "WorkflowName" = EXCLUDED."WorkflowName",
                "Status" = EXCLUDED."Status"
            """, ct);

    public async Task<GitHubBackfillStatus> GetBackfillStatusAsync(string repo, CancellationToken ct = default) =>
        new(
            await ctx.GitHubPullRequests.AsNoTracking().AnyAsync(p => p.Repo == repo, ct),
            await ctx.GitHubCommits.AsNoTracking().AnyAsync(c => c.Repo == repo, ct),
            await ctx.GitHubWorkflowRuns.AsNoTracking().AnyAsync(r => r.Repo == repo, ct));

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
