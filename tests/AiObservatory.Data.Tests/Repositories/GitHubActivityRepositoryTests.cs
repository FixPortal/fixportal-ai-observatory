using AiObservatory.Data;
using AiObservatory.Data.Repositories;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Npgsql;
using Xunit;

namespace AiObservatory.Data.Tests.Repositories;

// Requires TEST_DB_CONNECTION env var pointing at a real PostgreSQL instance.
// Own database (aiobs_test_github), same isolation rationale as AdversarialReviewRepositoryTests.
public class GitHubActivityRepositoryTests : IAsyncLifetime
{
    private string _connStr = null!;
    private AiObservatoryDbContext _ctx = null!;
    private IGitHubActivityRepository _repo = null!;

    public async ValueTask InitializeAsync()
    {
        var baseConn = Environment.GetEnvironmentVariable("TEST_DB_CONNECTION")
            ?? "Host=localhost;Database=aiobs_test;Username=postgres;Password=postgres";
        _connStr = new NpgsqlConnectionStringBuilder(baseConn) { Database = "aiobs_test_github" }.ConnectionString;
        var options = new DbContextOptionsBuilder<AiObservatoryDbContext>()
            .UseNpgsql(_connStr, o => o.UseNodaTime())
            .Options;
        _ctx = new AiObservatoryDbContext(options);
        await _ctx.Database.MigrateAsync();
        _repo = new GitHubActivityRepository(_ctx);
    }

    public async ValueTask DisposeAsync()
    {
        if (_connStr.Contains("_test", StringComparison.OrdinalIgnoreCase))
        {
            await _ctx.Database.EnsureDeletedAsync();
        }
        await _ctx.DisposeAsync();
    }

    private static GitHubPullRequestRecord Pr(string state = "open", int reviewCount = 0, Instant? firstReviewAt = null) =>
        new("fix-portal/example", 42, "Add feature", "chris", state,
            Instant.FromUtc(2026, 7, 1, 9, 0), null, null, firstReviewAt, reviewCount);

    [Fact]
    public async Task UpsertPullRequestAsync_WhenNew_Inserts()
    {
        await _repo.UpsertPullRequestAsync(Pr(), Instant.FromUtc(2026, 7, 1, 10, 0), TestContext.Current.CancellationToken);

        var stored = await _ctx.GitHubPullRequests.SingleAsync(TestContext.Current.CancellationToken);
        stored.State.Should().Be("open");
        stored.ReviewCount.Should().Be(0);
    }

    [Fact]
    public async Task UpsertPullRequestAsync_WhenRepolledWithNewState_UpdatesInPlace()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repo.UpsertPullRequestAsync(Pr(), Instant.FromUtc(2026, 7, 1, 10, 0), ct);

        await _repo.UpsertPullRequestAsync(
            Pr(state: "merged", reviewCount: 2, firstReviewAt: Instant.FromUtc(2026, 7, 1, 11, 0)),
            Instant.FromUtc(2026, 7, 2, 10, 0), ct);

        var stored = await _ctx.GitHubPullRequests.SingleAsync(ct);
        stored.State.Should().Be("merged");
        stored.ReviewCount.Should().Be(2);
        stored.FirstReviewAt.Should().Be(Instant.FromUtc(2026, 7, 1, 11, 0));
        // IngestedAt is set only on first insert — a repoll must not disturb it.
        stored.IngestedAt.Should().Be(Instant.FromUtc(2026, 7, 1, 10, 0));
    }

    [Fact]
    public async Task UpsertCommitAsync_WhenRepolled_IsNoOpNotDuplicate()
    {
        var ct = TestContext.Current.CancellationToken;
        var commit = new GitHubCommitRecord("fix-portal/example", "abc123", "chris", Instant.FromUtc(2026, 7, 1, 9, 0), 10, 2);

        await _repo.UpsertCommitAsync(commit, Instant.FromUtc(2026, 7, 1, 10, 0), ct);
        await _repo.UpsertCommitAsync(commit, Instant.FromUtc(2026, 7, 2, 10, 0), ct);

        var count = await _ctx.GitHubCommits.CountAsync(ct);
        count.Should().Be(1);
    }

    [Fact]
    public async Task UpsertWorkflowRunAsync_WhenStatusChanges_UpdatesStatus()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = new GitHubWorkflowRunRecord("fix-portal/example", 999, "ci.yml", "in_progress", Instant.FromUtc(2026, 7, 1, 9, 0));

        await _repo.UpsertWorkflowRunAsync(run, Instant.FromUtc(2026, 7, 1, 9, 0), ct);
        await _repo.UpsertWorkflowRunAsync(run with { Status = "success" }, Instant.FromUtc(2026, 7, 1, 9, 5), ct);

        var stored = await _ctx.GitHubWorkflowRuns.SingleAsync(ct);
        stored.Status.Should().Be("success");
    }

    [Fact]
    public async Task GetBackfillStatusAsync_WhenNoRowsForRepo_AllFalse()
    {
        var result = await _repo.GetBackfillStatusAsync("fix-portal/never-seen", TestContext.Current.CancellationToken);
        result.HasPullRequests.Should().BeFalse();
        result.HasCommits.Should().BeFalse();
        result.HasWorkflowRuns.Should().BeFalse();
    }

    [Fact]
    public async Task GetBackfillStatusAsync_WhenOnlyCommitsExist_OnlyCommitsTrue()
    {
        // Regression case: a crash/rate-limit abort after PRs ingested but before
        // commits/runs must not permanently skip THEIR backfill — each table's
        // status is independent, not OR'd into one repo-wide bool.
        var ct = TestContext.Current.CancellationToken;
        await _repo.UpsertCommitAsync(
            new GitHubCommitRecord("fix-portal/example", "abc123", "chris", Instant.FromUtc(2026, 7, 1, 9, 0), 1, 0),
            Instant.FromUtc(2026, 7, 1, 9, 0), ct);

        var result = await _repo.GetBackfillStatusAsync("fix-portal/example", ct);
        result.HasPullRequests.Should().BeFalse();
        result.HasCommits.Should().BeTrue();
        result.HasWorkflowRuns.Should().BeFalse();
    }
}
