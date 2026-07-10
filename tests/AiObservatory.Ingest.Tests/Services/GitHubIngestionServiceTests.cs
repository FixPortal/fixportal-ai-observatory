using AiObservatory.Data.Repositories;
using AiObservatory.Ingest.Services.GitHub;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace AiObservatory.Ingest.Tests.Services;

public class GitHubIngestionServiceTests
{
    private static readonly Instant FixedNow = Instant.FromUtc(2026, 7, 1, 12, 0);
    private static readonly FakeClock Clock = new(FixedNow);

    private static IOptions<IngestOptions> Options(params string[] repos) =>
        Microsoft.Extensions.Options.Options.Create(new IngestOptions { GitHubRepoAllowlist = repos });

    private static readonly GitHubBackfillStatus NoPriorData = new(false, false, false);
    private static readonly GitHubBackfillStatus FullyBackfilled = new(true, true, true);

    [Fact]
    public async Task IngestSinceAsync_WhenRepoHasNoPriorData_UsesThirtyDayBackfillWindow()
    {
        var client = Substitute.For<IGitHubActivityClient>();
        var repo = Substitute.For<IGitHubActivityRepository>();
        repo.GetBackfillStatusAsync("fix-portal/example", Arg.Any<CancellationToken>()).Returns(NoPriorData);
        client.GetPullRequestsAsync("fix-portal/example", Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns([]);
        client.GetCommitsAsync("fix-portal/example", Arg.Any<LocalDate>(), Arg.Any<CancellationToken>()).Returns([]);
        client.GetWorkflowRunsAsync("fix-portal/example", Arg.Any<LocalDate>(), Arg.Any<CancellationToken>()).Returns([]);

        var sut = new GitHubIngestionService(client, repo, Options("fix-portal/example"), NullLogger<GitHubIngestionService>.Instance, Clock);

        var pollDate = new LocalDate(2026, 7, 1);
        await sut.IngestSinceAsync(pollDate, TestContext.Current.CancellationToken);

        await client.Received(1).GetPullRequestsAsync("fix-portal/example", pollDate.PlusDays(-30), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IngestSinceAsync_WhenRepoAlreadyHasData_UsesGivenDateNotBackfill()
    {
        var client = Substitute.For<IGitHubActivityClient>();
        var repo = Substitute.For<IGitHubActivityRepository>();
        repo.GetBackfillStatusAsync("fix-portal/example", Arg.Any<CancellationToken>()).Returns(FullyBackfilled);
        client.GetPullRequestsAsync("fix-portal/example", Arg.Any<LocalDate>(), Arg.Any<CancellationToken>()).Returns([]);
        client.GetCommitsAsync("fix-portal/example", Arg.Any<LocalDate>(), Arg.Any<CancellationToken>()).Returns([]);
        client.GetWorkflowRunsAsync("fix-portal/example", Arg.Any<LocalDate>(), Arg.Any<CancellationToken>()).Returns([]);

        var sut = new GitHubIngestionService(client, repo, Options("fix-portal/example"), NullLogger<GitHubIngestionService>.Instance, Clock);

        var pollDate = new LocalDate(2026, 7, 1);
        await sut.IngestSinceAsync(pollDate, TestContext.Current.CancellationToken);

        await client.Received(1).GetPullRequestsAsync("fix-portal/example", pollDate, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IngestSinceAsync_WhenOnlyPullRequestsBackfilled_CommitsAndRunsStillGetThirtyDayWindow()
    {
        // Regression case for the bug this fix closes: a repo whose PRs backfilled
        // on an earlier cycle (e.g. before a crash/rate-limit abort) must still get
        // the one-time 30-day backfill for commits and runs — not skip it just
        // because the repo has SOME data.
        var client = Substitute.For<IGitHubActivityClient>();
        var repo = Substitute.For<IGitHubActivityRepository>();
        repo.GetBackfillStatusAsync("fix-portal/example", Arg.Any<CancellationToken>())
            .Returns(new GitHubBackfillStatus(HasPullRequests: true, HasCommits: false, HasWorkflowRuns: false));
        client.GetPullRequestsAsync("fix-portal/example", Arg.Any<LocalDate>(), Arg.Any<CancellationToken>()).Returns([]);
        client.GetCommitsAsync("fix-portal/example", Arg.Any<LocalDate>(), Arg.Any<CancellationToken>()).Returns([]);
        client.GetWorkflowRunsAsync("fix-portal/example", Arg.Any<LocalDate>(), Arg.Any<CancellationToken>()).Returns([]);

        var sut = new GitHubIngestionService(client, repo, Options("fix-portal/example"), NullLogger<GitHubIngestionService>.Instance, Clock);

        var pollDate = new LocalDate(2026, 7, 1);
        await sut.IngestSinceAsync(pollDate, TestContext.Current.CancellationToken);

        await client.Received(1).GetPullRequestsAsync("fix-portal/example", pollDate, Arg.Any<CancellationToken>());
        await client.Received(1).GetCommitsAsync("fix-portal/example", pollDate.PlusDays(-30), Arg.Any<CancellationToken>());
        await client.Received(1).GetWorkflowRunsAsync("fix-portal/example", pollDate.PlusDays(-30), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IngestSinceAsync_PersistsEveryFetchedRecordViaRepository()
    {
        var client = Substitute.For<IGitHubActivityClient>();
        var repo = Substitute.For<IGitHubActivityRepository>();
        repo.GetBackfillStatusAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(FullyBackfilled);
        var pr = new GitHubPullRequestRecord("fix-portal/example", 1, "t", "chris", "open", Instant.FromUtc(2026, 7, 1, 9, 0), null, null, null, 0);
        client.GetPullRequestsAsync(Arg.Any<string>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>()).Returns([pr]);
        client.GetCommitsAsync(Arg.Any<string>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>()).Returns([]);
        client.GetWorkflowRunsAsync(Arg.Any<string>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>()).Returns([]);

        var sut = new GitHubIngestionService(client, repo, Options("fix-portal/example"), NullLogger<GitHubIngestionService>.Instance, Clock);
        await sut.IngestSinceAsync(new LocalDate(2026, 7, 1), TestContext.Current.CancellationToken);

        await repo.Received(1).UpsertPullRequestAsync(pr, FixedNow, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IngestSinceAsync_WhenOneRepoThrows403_SkipsItAndContinuesWithNextRepo()
    {
        var client = Substitute.For<IGitHubActivityClient>();
        var repo = Substitute.For<IGitHubActivityRepository>();
        repo.GetBackfillStatusAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(FullyBackfilled);
        client.GetPullRequestsAsync("fix-portal/broken", Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<GitHubPullRequestRecord>>(new HttpRequestException("403")));
        client.GetPullRequestsAsync("fix-portal/ok", Arg.Any<LocalDate>(), Arg.Any<CancellationToken>()).Returns([]);
        client.GetCommitsAsync(Arg.Any<string>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>()).Returns([]);
        client.GetWorkflowRunsAsync(Arg.Any<string>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>()).Returns([]);

        var sut = new GitHubIngestionService(client, repo, Options("fix-portal/broken", "fix-portal/ok"), NullLogger<GitHubIngestionService>.Instance, Clock);

        var failedCount = await sut.IngestSinceAsync(new LocalDate(2026, 7, 1), TestContext.Current.CancellationToken);

        await client.Received(1).GetPullRequestsAsync("fix-portal/ok", Arg.Any<LocalDate>(), Arg.Any<CancellationToken>());
        failedCount.Should().Be(1);
    }

    [Fact]
    public async Task IngestSinceAsync_WhenOneRepoTimesOut_SkipsItAndContinuesWithNextRepo()
    {
        var client = Substitute.For<IGitHubActivityClient>();
        var repo = Substitute.For<IGitHubActivityRepository>();
        repo.GetBackfillStatusAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(FullyBackfilled);
        client.GetPullRequestsAsync("fix-portal/slow", Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<GitHubPullRequestRecord>>(new TaskCanceledException("client timeout")));
        client.GetPullRequestsAsync("fix-portal/ok", Arg.Any<LocalDate>(), Arg.Any<CancellationToken>()).Returns([]);
        client.GetCommitsAsync(Arg.Any<string>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>()).Returns([]);
        client.GetWorkflowRunsAsync(Arg.Any<string>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>()).Returns([]);

        var sut = new GitHubIngestionService(client, repo, Options("fix-portal/slow", "fix-portal/ok"), NullLogger<GitHubIngestionService>.Instance, Clock);

        var failedCount = await sut.IngestSinceAsync(new LocalDate(2026, 7, 1), TestContext.Current.CancellationToken);

        await client.Received(1).GetPullRequestsAsync("fix-portal/ok", Arg.Any<LocalDate>(), Arg.Any<CancellationToken>());
        failedCount.Should().Be(1);
    }

    [Fact]
    public async Task IngestSinceAsync_WhenCancellationTokenIsCancelled_RethrowsCancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var client = Substitute.For<IGitHubActivityClient>();
        var repo = Substitute.For<IGitHubActivityRepository>();
        repo.GetBackfillStatusAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromCanceled<GitHubBackfillStatus>(cts.Token));

        var sut = new GitHubIngestionService(client, repo, Options("fix-portal/example"), NullLogger<GitHubIngestionService>.Instance, Clock);

        var act = () => sut.IngestSinceAsync(new LocalDate(2026, 7, 1), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task IngestSinceAsync_WhenRateLimitExceeded_AbortsRemainingReposWithoutThrowing()
    {
        var client = Substitute.For<IGitHubActivityClient>();
        var repo = Substitute.For<IGitHubActivityRepository>();
        repo.GetBackfillStatusAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(FullyBackfilled);
        client.GetPullRequestsAsync("fix-portal/first", Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<GitHubPullRequestRecord>>(new GitHubRateLimitExceededException(10)));
        client.GetCommitsAsync(Arg.Any<string>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>()).Returns([]);
        client.GetWorkflowRunsAsync(Arg.Any<string>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>()).Returns([]);

        var sut = new GitHubIngestionService(client, repo, Options("fix-portal/first", "fix-portal/second"), NullLogger<GitHubIngestionService>.Instance, Clock);

        var failedCount = await sut.IngestSinceAsync(new LocalDate(2026, 7, 1), TestContext.Current.CancellationToken);

        await client.DidNotReceive().GetPullRequestsAsync("fix-portal/second", Arg.Any<LocalDate>(), Arg.Any<CancellationToken>());
        failedCount.Should().Be(0);
    }

    [Fact]
    public async Task IngestSinceAsync_WhenRepoAlreadyFailedThenRateLimitHit_ReturnsPriorFailureCount()
    {
        var client = Substitute.For<IGitHubActivityClient>();
        var repo = Substitute.For<IGitHubActivityRepository>();
        repo.GetBackfillStatusAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(FullyBackfilled);
        client.GetPullRequestsAsync("fix-portal/broken", Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<GitHubPullRequestRecord>>(new HttpRequestException("403")));
        client.GetPullRequestsAsync("fix-portal/rate-limited", Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<GitHubPullRequestRecord>>(new GitHubRateLimitExceededException(10)));
        client.GetCommitsAsync(Arg.Any<string>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>()).Returns([]);
        client.GetWorkflowRunsAsync(Arg.Any<string>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>()).Returns([]);

        var sut = new GitHubIngestionService(
            client, repo, Options("fix-portal/broken", "fix-portal/rate-limited", "fix-portal/never-reached"),
            NullLogger<GitHubIngestionService>.Instance, Clock);

        var failedCount = await sut.IngestSinceAsync(new LocalDate(2026, 7, 1), TestContext.Current.CancellationToken);

        await client.DidNotReceive().GetPullRequestsAsync("fix-portal/never-reached", Arg.Any<LocalDate>(), Arg.Any<CancellationToken>());
        failedCount.Should().Be(1);
    }
}
