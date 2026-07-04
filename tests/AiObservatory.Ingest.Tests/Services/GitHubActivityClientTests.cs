using System.Net;
using System.Net.Http.Headers;
using AiObservatory.Ingest.Services.GitHub;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;

namespace AiObservatory.Ingest.Tests.Services;

public class GitHubActivityClientTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> RequestedUrls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            RequestedUrls.Add(request.RequestUri!.ToString());
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage JsonResponse(string json, int rateLimitRemaining = 4999)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
        response.Headers.Add("X-RateLimit-Remaining", rateLimitRemaining.ToString());
        return response;
    }

    private static GitHubActivityClient CreateSut(StubHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com") }, NullLogger<GitHubActivityClient>.Instance);

    [Fact]
    public async Task GetPullRequestsAsync_ParsesFieldsAndFetchesReviewCount()
    {
        var handler = new StubHandler(req =>
        {
            if (req.RequestUri!.ToString().Contains("/reviews"))
            {
                return JsonResponse("""[{"submitted_at":"2026-07-01T12:00:00Z"},{"submitted_at":"2026-07-01T14:00:00Z"}]""");
            }
            return JsonResponse("""
                [{"number":42,"title":"Add feature","user":{"login":"chris"},"state":"open",
                  "created_at":"2026-07-01T09:00:00Z","merged_at":null,"closed_at":null}]
                """);
        });
        var sut = CreateSut(handler);

        var result = await sut.GetPullRequestsAsync("fix-portal/example", new LocalDate(2026, 7, 1), TestContext.Current.CancellationToken);

        var pr = result.Single();
        pr.Number.Should().Be(42);
        pr.Author.Should().Be("chris");
        pr.State.Should().Be("open");
        pr.ReviewCount.Should().Be(2);
        pr.FirstReviewAt.Should().Be(Instant.FromUtc(2026, 7, 1, 12, 0));
    }

    [Fact]
    public async Task GetPullRequestsAsync_WhenNoReviews_ReviewCountZeroAndFirstReviewAtNull()
    {
        var handler = new StubHandler(req =>
        {
            if (req.RequestUri!.ToString().Contains("/reviews")) return JsonResponse("[]");
            return JsonResponse("""
                [{"number":1,"title":"WIP","user":{"login":"chris"},"state":"open",
                  "created_at":"2026-07-01T09:00:00Z","merged_at":null,"closed_at":null}]
                """);
        });
        var sut = CreateSut(handler);

        var result = await sut.GetPullRequestsAsync("fix-portal/example", new LocalDate(2026, 7, 1), TestContext.Current.CancellationToken);

        result.Single().ReviewCount.Should().Be(0);
        result.Single().FirstReviewAt.Should().BeNull();
    }

    [Fact]
    public async Task GetPullRequestsAsync_PaginatesUntilShortPage()
    {
        var callCount = 0;
        var handler = new StubHandler(req =>
        {
            if (req.RequestUri!.ToString().Contains("/reviews")) return JsonResponse("[]");
            callCount++;
            // Page 1: a full 100-row page (forces a second page request); page 2: short page, stop.
            if (req.RequestUri!.ToString().Contains("page=2"))
            {
                return JsonResponse("""[{"number":2,"title":"b","user":{"login":"chris"},"state":"open","created_at":"2026-07-01T09:00:00Z","merged_at":null,"closed_at":null}]""");
            }
            var page = string.Join(",", Enumerable.Range(1, 100).Select(i =>
                $$"""{"number":{{i}},"title":"t","user":{"login":"chris"},"state":"open","created_at":"2026-07-01T09:00:00Z","merged_at":null,"closed_at":null}"""));
            return JsonResponse($"[{page}]");
        });
        var sut = CreateSut(handler);

        var result = await sut.GetPullRequestsAsync("fix-portal/example", new LocalDate(2026, 7, 1), TestContext.Current.CancellationToken);

        result.Should().HaveCount(101); // 100 from page 1 + 1 from page 2
    }

    [Fact]
    public async Task GetPullRequestsAsync_WhenRateLimitNearZero_ThrowsRateLimitException()
    {
        var handler = new StubHandler(_ => JsonResponse("[]", rateLimitRemaining: 10));
        var sut = CreateSut(handler);

        var act = () => sut.GetPullRequestsAsync("fix-portal/example", new LocalDate(2026, 7, 1), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<GitHubRateLimitExceededException>();
    }

    [Fact]
    public async Task GetPullRequestsAsync_WhenRepoForbidden_ThrowsHttpRequestException()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        var sut = CreateSut(handler);

        var act = () => sut.GetPullRequestsAsync("fix-portal/private-no-access", new LocalDate(2026, 7, 1), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task GetCommitsAsync_FetchesShaAuthorDateAndChurnStats()
    {
        var handler = new StubHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("/commits/abc123"))
            {
                return JsonResponse("""{"sha":"abc123","stats":{"additions":10,"deletions":2}}""");
            }
            return JsonResponse("""
                [{"sha":"abc123","commit":{"author":{"name":"chris","date":"2026-07-01T09:00:00Z"}}}]
                """);
        });
        var sut = CreateSut(handler);

        var result = await sut.GetCommitsAsync("fix-portal/example", new LocalDate(2026, 7, 1), TestContext.Current.CancellationToken);

        var commit = result.Single();
        commit.Sha.Should().Be("abc123");
        commit.Author.Should().Be("chris");
        commit.Additions.Should().Be(10);
        commit.Deletions.Should().Be(2);
    }

    [Fact]
    public async Task GetCommitsAsync_PassesSinceAsQueryParam()
    {
        var handler = new StubHandler(req =>
        {
            if (req.RequestUri!.ToString().Contains("/commits/")) return JsonResponse("""{"sha":"x","stats":{"additions":0,"deletions":0}}""");
            return JsonResponse("[]");
        });
        var sut = CreateSut(handler);

        await sut.GetCommitsAsync("fix-portal/example", new LocalDate(2026, 7, 1), TestContext.Current.CancellationToken);

        handler.RequestedUrls.Should().Contain(u => u.Contains("since=2026-07-01"));
    }
}
