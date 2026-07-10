using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace AiObservatory.Data.Repositories;

public class GitHubActivityRepository(AiObservatoryDbContext ctx) : IGitHubActivityRepository
{
    public Task UpsertPullRequestAsync(GitHubPullRequestRecord record, Instant ingestedAt, CancellationToken ct = default) =>
        ctx.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "GitHubPullRequests"
                ("Id", "Repo", "Number", "Title", "Author", "State", "CreatedAt", "MergedAt", "ClosedAt", "FirstReviewAt", "ReviewCount", "IngestedAt")
            VALUES
                ({Guid.NewGuid()}, {Truncate(record.Repo, 200)}, {record.Number}, {Truncate(record.Title, 500)}, {Truncate(record.Author, 200)}, {Truncate(record.State, 20)}, {record.CreatedAt}, {record.MergedAt}, {record.ClosedAt}, {record.FirstReviewAt}, {record.ReviewCount}, {ingestedAt})
            ON CONFLICT ("Repo", "Number") DO UPDATE SET
                "Title" = EXCLUDED."Title",
                "State" = EXCLUDED."State",
                "MergedAt" = EXCLUDED."MergedAt",
                "ClosedAt" = EXCLUDED."ClosedAt",
                "FirstReviewAt" = COALESCE(EXCLUDED."FirstReviewAt", "GitHubPullRequests"."FirstReviewAt"),
                "ReviewCount" = GREATEST(EXCLUDED."ReviewCount", "GitHubPullRequests"."ReviewCount")
            """, ct);

    public Task UpsertCommitAsync(GitHubCommitRecord record, Instant ingestedAt, CancellationToken ct = default) =>
        ctx.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "GitHubCommits"
                ("Id", "Repo", "Sha", "Author", "CommittedAt", "Additions", "Deletions", "IngestedAt")
            VALUES
                ({Guid.NewGuid()}, {Truncate(record.Repo, 200)}, {Truncate(record.Sha, 64)}, {Truncate(record.Author, 200)}, {record.CommittedAt}, {record.Additions}, {record.Deletions}, {ingestedAt})
            ON CONFLICT ("Repo", "Sha") DO NOTHING
            """, ct);

    public Task UpsertWorkflowRunAsync(GitHubWorkflowRunRecord record, Instant ingestedAt, CancellationToken ct = default) =>
        ctx.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "GitHubWorkflowRuns"
                ("Id", "Repo", "RunId", "WorkflowName", "Status", "CreatedAt", "IngestedAt")
            VALUES
                ({Guid.NewGuid()}, {Truncate(record.Repo, 200)}, {record.RunId}, {Truncate(record.WorkflowName, 200)}, {Truncate(record.Status, 20)}, {record.CreatedAt}, {ingestedAt})
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
