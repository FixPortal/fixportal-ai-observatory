using NodaTime;

namespace AiObservatory.Data.Repositories;

public record GitHubPullRequestRecord(
    string Repo, int Number, string Title, string Author, string State,
    Instant CreatedAt, Instant? MergedAt, Instant? ClosedAt, Instant? FirstReviewAt, int ReviewCount);

public record GitHubCommitRecord(
    string Repo, string Sha, string Author, Instant CommittedAt, int Additions, int Deletions);

public record GitHubWorkflowRunRecord(
    string Repo, long RunId, string WorkflowName, string Status, Instant CreatedAt);

// Per-table backfill state for a repo. Checked independently — not folded into a
// single "has any data" bool — because a repo can have PRs backfilled while a
// crash or rate-limit abort left commits/runs never backfilled; a single OR'd
// bool would see "some data exists" and permanently skip their one-time pull.
public record GitHubBackfillStatus(bool HasPullRequests, bool HasCommits, bool HasWorkflowRuns);

public interface IGitHubActivityRepository
{
    Task UpsertPullRequestAsync(GitHubPullRequestRecord record, Instant ingestedAt, CancellationToken ct = default);
    Task UpsertCommitAsync(GitHubCommitRecord record, Instant ingestedAt, CancellationToken ct = default);
    Task UpsertWorkflowRunAsync(GitHubWorkflowRunRecord record, Instant ingestedAt, CancellationToken ct = default);
    Task<GitHubBackfillStatus> GetBackfillStatusAsync(string repo, CancellationToken ct = default);
}
