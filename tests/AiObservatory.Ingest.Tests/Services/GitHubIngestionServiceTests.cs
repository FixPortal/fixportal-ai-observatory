using AiObservatory.Data.Repositories;
using AiObservatory.Ingest.Services.GitHub;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NSubstitute;

namespace AiObservatory.Ingest.Tests.Services;

public class GitHubIngestionServiceTests
{
    private static IOptions<IngestOptions> Options(params string[] repos) =>
        Microsoft.Extensions.Options.Options.Create(new IngestOptions { GitHubRepoAllowlist = repos });

    [Fact]
    public async Task IngestSinceAsync_WhenRepoHasNoPriorData_UsesThirtyDayBackfillWindow()
    {
        var client = Substitute.For<IGitHubActivityClient>();
        var repo = Substitute.For<IGitHubActivityRepository>();
        repo.HasAnyDataForRepoAsync("fix-portal/example", Arg.Any<CancellationToken>()).Returns(false);
        client.GetPullRequestsAsync("fix-portal/example", Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns([]);
        client.GetCommitsAsync("fix-portal/example", Arg.Any<LocalDate>(), Arg.Any<CancellationToken>()).Returns([]);
        client.GetWorkflowRunsAsync("fix-portal/example", Arg.Any<LocalDate>(), Arg.Any<CancellationToken>()).Returns([]);

        var sut = new GitHubIngestionService(client, repo, Options("fix-portal/example"), NullLogger<GitHubIngestionService>.Instance);

        var pollDate = new LocalDate(2026, 7, 1);
        await sut.IngestSinceAsync(pollDate, TestContext.Current.CancellationToken);

        await client.Received(1).GetPullRequestsAsync("fix-portal/example", pollDate.PlusDays(-30), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IngestSinceAsync_WhenRepoAlreadyHasData_UsesGivenDateNotBackfill()
    {
        var client = Substitute.For<IGitHubActivityClient>();
        var repo = Substitute.For<IGitHubActivityRepository>();
        repo.HasAnyDataForRepoAsync("fix-portal/example", Arg.Any<CancellationToken>()).Returns(true);
        client.GetPullRequestsAsync("fix-portal/example", Arg.Any<LocalDate>(), Arg.Any<CancellationToken>()).Returns([]);
        client.GetCommitsAsync("fix-portal/example", Arg.Any<LocalDate>(), Arg.Any<CancellationToken>()).Returns([]);
        client.GetWorkflowRunsAsync("fix-portal/example", Arg.Any<LocalDate>(), Arg.Any<CancellationToken>()).Returns([]);

        var sut = new GitHubIngestionService(client, repo, Options("fix-portal/example"), NullLogger<GitHubIngestionService>.Instance);

        var pollDate = new LocalDate(2026, 7, 1);
        await sut.IngestSinceAsync(pollDate, TestContext.Current.CancellationToken);

        await client.Received(1).GetPullRequestsAsync("fix-portal/example", pollDate, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IngestSinceAsync_PersistsEveryFetchedRecordViaRepository()
    {
        var client = Substitute.For<IGitHubActivityClient>();
        var repo = Substitute.For<IGitHubActivityRepository>();
        repo.HasAnyDataForRepoAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        var pr = new GitHubPullRequestRecord("fix-portal/example", 1, "t", "chris", "open", Instant.FromUtc(2026, 7, 1, 9, 0), null, null, null, 0);
        client.GetPullRequestsAsync(Arg.Any<string>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>()).Returns([pr]);
        client.GetCommitsAsync(Arg.Any<string>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>()).Returns([]);
        client.GetWorkflowRunsAsync(Arg.Any<string>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>()).Returns([]);

        var sut = new GitHubIngestionService(client, repo, Options("fix-portal/example"), NullLogger<GitHubIngestionService>.Instance);
        await sut.IngestSinceAsync(new LocalDate(2026, 7, 1), TestContext.Current.CancellationToken);

        await repo.Received(1).UpsertPullRequestAsync(pr, Arg.Any<Instant>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IngestSinceAsync_WhenOneRepoThrows403_SkipsItAndContinuesWithNextRepo()
    {
        var client = Substitute.For<IGitHubActivityClient>();
        var repo = Substitute.For<IGitHubActivityRepository>();
        repo.HasAnyDataForRepoAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        client.GetPullRequestsAsync("fix-portal/broken", Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<GitHubPullRequestRecord>>(new HttpRequestException("403")));
        client.GetPullRequestsAsync("fix-portal/ok", Arg.Any<LocalDate>(), Arg.Any<CancellationToken>()).Returns([]);
        client.GetCommitsAsync(Arg.Any<string>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>()).Returns([]);
        client.GetWorkflowRunsAsync(Arg.Any<string>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>()).Returns([]);

        var sut = new GitHubIngestionService(client, repo, Options("fix-portal/broken", "fix-portal/ok"), NullLogger<GitHubIngestionService>.Instance);

        var failedCount = await sut.IngestSinceAsync(new LocalDate(2026, 7, 1), TestContext.Current.CancellationToken);

        await client.Received(1).GetPullRequestsAsync("fix-portal/ok", Arg.Any<LocalDate>(), Arg.Any<CancellationToken>());
        failedCount.Should().Be(1);
    }

    [Fact]
    public async Task IngestSinceAsync_WhenRateLimitExceeded_AbortsRemainingReposWithoutThrowing()
    {
        var client = Substitute.For<IGitHubActivityClient>();
        var repo = Substitute.For<IGitHubActivityRepository>();
        repo.HasAnyDataForRepoAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        client.GetPullRequestsAsync("fix-portal/first", Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<GitHubPullRequestRecord>>(new GitHubRateLimitExceededException(10)));
        client.GetCommitsAsync(Arg.Any<string>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>()).Returns([]);
        client.GetWorkflowRunsAsync(Arg.Any<string>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>()).Returns([]);

        var sut = new GitHubIngestionService(client, repo, Options("fix-portal/first", "fix-portal/second"), NullLogger<GitHubIngestionService>.Instance);

        var failedCount = await sut.IngestSinceAsync(new LocalDate(2026, 7, 1), TestContext.Current.CancellationToken);

        await client.DidNotReceive().GetPullRequestsAsync("fix-portal/second", Arg.Any<LocalDate>(), Arg.Any<CancellationToken>());
        failedCount.Should().Be(0);
    }
}
