using System.Net;
using System.Net.Http.Json;
using AiObservatory.Data;
using AiObservatory.Data.Entities;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace AiObservatory.Api.Tests;

/// <summary>
/// GET /api/github/commits/summary regression coverage. The endpoint 500'd in production —
/// EF Core could not translate a GroupBy/Select projecting straight into the
/// GitHubCommitSummaryResponse record with two Sum aggregates (InvalidOperationException at
/// request time, not startup). The sibling /github/prs and /github/ci routes happened to
/// avoid the same translation trap and the helper-method unit tests never call the query
/// itself, so nothing caught this before it shipped — only a real-Postgres WAF test does.
/// </summary>
[Trait("Category", "Integration")]
[Collection("ApiFactory")]
public class GitHubActivityEndpointsWafTests(AiObservatoryApiFactory factory)
{
    [Fact]
    public async Task GetCommitsSummary_AggregatesAdditionsAndDeletionsPerRepo()
    {
        // Unique out-of-range window (year 2019) so this test's own rows are unambiguously
        // identifiable regardless of what other tests in the shared collection have added.
        var committedAt = Instant.FromUtc(2019, 5, 29, 12, 0);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
            db.GitHubCommits.AddRange(
                new GitHubCommit
                {
                    Repo = "FixPortal/waf-commit-summary-test", Sha = "a1", Author = "chris",
                    CommittedAt = committedAt, Additions = 10, Deletions = 2, IngestedAt = committedAt,
                },
                new GitHubCommit
                {
                    Repo = "FixPortal/waf-commit-summary-test", Sha = "a2", Author = "chris",
                    CommittedAt = committedAt, Additions = 5, Deletions = 1, IngestedAt = committedAt,
                });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var client = factory.CreateAdminClient();
        var response = await client.GetAsync(
            "/api/github/commits/summary?from=2019-05-29&to=2019-05-29", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var summaries = await response.Content.ReadFromJsonAsync<List<GitHubCommitSummaryRow>>(TestContext.Current.CancellationToken);
        var row = summaries.Should().ContainSingle(s => s.Repo == "FixPortal/waf-commit-summary-test").Which;
        row.CommitCount.Should().Be(2);
        row.Additions.Should().Be(15);
        row.Deletions.Should().Be(3);
    }

    /// <summary>
    /// The repo allowlist must match the casing the INGEST WORKER writes, not the casing the
    /// allowlist config happens to use. <c>GitHubIngestionService</c> normalises every repo
    /// with <c>ToLowerInvariant</c>, so rows land as "fixportal/x" while
    /// <c>AllowedProjectOwners</c> holds the display form "FixPortal" — and an ordinal
    /// comparison between those matches nothing.
    /// <para>
    /// That silently filtered out every ingested GitHub row in production. It survived
    /// because the worker had never started in Azure, so there was nothing to drop, and
    /// because the test above seeds "FixPortal/..." by hand — encoding the filter's own
    /// assumption instead of the producer's real output. This test seeds what the worker
    /// actually writes.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("fixportal/waf-casing-test")]   // what the worker writes
    [InlineData("FixPortal/waf-casing-test")]   // display casing, must keep working
    public async Task GetCommitsSummary_MatchesTheRepoAllowlistRegardlessOfCase(string repo)
    {
        var committedAt = Instant.FromUtc(2019, 6, 14, 12, 0);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
            db.GitHubCommits.Add(new GitHubCommit
            {
                Repo = repo, Sha = $"case-{Guid.NewGuid():N}", Author = "chris",
                CommittedAt = committedAt, Additions = 7, Deletions = 3, IngestedAt = committedAt,
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var client = factory.CreateAdminClient();
        var response = await client.GetAsync(
            "/api/github/commits/summary?from=2019-06-14&to=2019-06-14", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var summaries = await response.Content.ReadFromJsonAsync<List<GitHubCommitSummaryRow>>(
            TestContext.Current.CancellationToken);

        summaries.Should().Contain(s => s.Repo == repo,
            "a repo owned by an allowlisted org must survive the filter whatever its casing");
    }

    /// <summary>A repo outside the allowlist must still be excluded — the fix widens casing, not scope.</summary>
    [Fact]
    public async Task GetCommitsSummary_StillExcludesRepositoriesOutsideTheAllowlist()
    {
        var committedAt = Instant.FromUtc(2019, 6, 15, 12, 0);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AiObservatoryDbContext>();
            db.GitHubCommits.Add(new GitHubCommit
            {
                Repo = "someoneelse/not-ours", Sha = $"out-{Guid.NewGuid():N}", Author = "chris",
                CommittedAt = committedAt, Additions = 1, Deletions = 1, IngestedAt = committedAt,
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var client = factory.CreateAdminClient();
        var response = await client.GetAsync(
            "/api/github/commits/summary?from=2019-06-15&to=2019-06-15", TestContext.Current.CancellationToken);

        var summaries = await response.Content.ReadFromJsonAsync<List<GitHubCommitSummaryRow>>(
            TestContext.Current.CancellationToken);

        summaries.Should().NotContain(s => s.Repo == "someoneelse/not-ours");
    }

    private sealed record GitHubCommitSummaryRow(string Repo, int CommitCount, int Additions, int Deletions);
}
