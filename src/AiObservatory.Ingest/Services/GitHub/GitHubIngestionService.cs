using AiObservatory.Data.Repositories;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;

namespace AiObservatory.Ingest.Services.GitHub;

// Called once per poll cycle (not once per lookback date, unlike the other
// providers) — GitHub's API takes a since-date range, so re-querying the same
// range per date would triple API calls for no benefit. See IGitHubActivityClient.
public class GitHubIngestionService(
    IGitHubActivityClient client,
    IGitHubActivityRepository repository,
    IOptions<IngestOptions> options,
    ILogger<GitHubIngestionService> logger)
{
    private const int BackfillDays = 30;

    public async Task IngestSinceAsync(LocalDate date, CancellationToken ct = default)
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        foreach (var repo in options.Value.GitHubRepoAllowlist)
        {
            try
            {
                var hasData = await repository.HasAnyDataForRepoAsync(repo, ct);
                var since = hasData ? date : date.PlusDays(-BackfillDays);

                var prs = await client.GetPullRequestsAsync(repo, since, ct);
                foreach (var pr in prs) await repository.UpsertPullRequestAsync(pr, now, ct);

                var commits = await client.GetCommitsAsync(repo, since, ct);
                foreach (var c in commits) await repository.UpsertCommitAsync(c, now, ct);

                var runs = await client.GetWorkflowRunsAsync(repo, since, ct);
                foreach (var r in runs) await repository.UpsertWorkflowRunAsync(r, now, ct);

                logger.LogInformation(
                    "GitHub: ingested {PrCount} PRs, {CommitCount} commits, {RunCount} workflow runs for {Repo}",
                    prs.Count, commits.Count, runs.Count, repo);
            }
            catch (GitHubRateLimitExceededException)
            {
                logger.LogWarning("GitHub: aborting remaining repos this poll cycle due to rate limit");
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "GitHub: failed to ingest {Repo}; skipping for this cycle", repo);
            }
        }
    }
}
