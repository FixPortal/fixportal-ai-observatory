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

    private sealed record GitHubCommitSummaryRow(string Repo, int CommitCount, int Additions, int Deletions);
}
