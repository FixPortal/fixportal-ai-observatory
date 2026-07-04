using AiObservatory.Data.Repositories;
using NodaTime;

namespace AiObservatory.Ingest.Services.GitHub;

public interface IGitHubActivityClient
{
    Task<IReadOnlyList<GitHubPullRequestRecord>> GetPullRequestsAsync(string repo, LocalDate since, CancellationToken ct = default);
    Task<IReadOnlyList<GitHubCommitRecord>> GetCommitsAsync(string repo, LocalDate since, CancellationToken ct = default);
    Task<IReadOnlyList<GitHubWorkflowRunRecord>> GetWorkflowRunsAsync(string repo, LocalDate since, CancellationToken ct = default);
}
