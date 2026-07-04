using NodaTime;

namespace AiObservatory.Data.Repositories;

public record GitHubPullRequestRecord(
    string Repo, int Number, string Title, string Author, string State,
    Instant CreatedAt, Instant? MergedAt, Instant? ClosedAt, Instant? FirstReviewAt, int ReviewCount);

public record GitHubCommitRecord(
    string Repo, string Sha, string Author, Instant CommittedAt, int Additions, int Deletions);

public record GitHubWorkflowRunRecord(
    string Repo, long RunId, string WorkflowName, string Status, Instant CreatedAt);

public interface IGitHubActivityRepository
{
    Task UpsertPullRequestAsync(GitHubPullRequestRecord record, Instant ingestedAt, CancellationToken ct = default);
    Task UpsertCommitAsync(GitHubCommitRecord record, Instant ingestedAt, CancellationToken ct = default);
    Task UpsertWorkflowRunAsync(GitHubWorkflowRunRecord record, Instant ingestedAt, CancellationToken ct = default);
    Task<bool> HasAnyDataForRepoAsync(string repo, CancellationToken ct = default);
}
