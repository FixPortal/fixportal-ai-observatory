using AiObservatory.Api.Services;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Repositories;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;

namespace AiObservatory.Api.Tests.Services;

public class AdversarialReviewServiceTests
{
    private readonly IAdversarialReviewRepository _repo = Substitute.For<IAdversarialReviewRepository>();
    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 6, 14, 10, 0));

    private AdversarialReviewService CreateSut() => new(_repo, _clock);

    private static AdversarialReviewRunRequest ValidRequest(
        string reviewer = "anthropic",
        string model = "claude-sonnet-4-6",
        int issuesRaised = 5,
        int issuesAccepted = 3,
        decimal costUsd = 0.12m,
        string runId = "2026-06-14T10:00:00Z") =>
        new(
            EventType: "adversarial-review-run",
            Reviewer: reviewer,
            Model: model,
            InputTokens: 1000,
            OutputTokens: 500,
            CostUsd: costUsd,
            ReviewDurationMs: 12345,
            IssuesRaised: issuesRaised,
            IssuesAccepted: issuesAccepted,
            RunId: runId
        );

    [Fact]
    public async Task RecordRun_valid_request_stores_run_and_returns_created()
    {
        var newId = Guid.NewGuid();
        _repo.RecordRunAsync(Arg.Any<AdversarialReviewRun>(), Arg.Any<CancellationToken>())
            .Returns((newId, IsDuplicate: false));

        var result = await CreateSut().RecordRunAsync(ValidRequest(), CancellationToken.None);

        result.Should().BeAssignableTo<IStatusCodeHttpResult>()
            .Which.StatusCode.Should().Be(201);
        await _repo.Received(1).RecordRunAsync(
            Arg.Is<AdversarialReviewRun>(r =>
                r.Reviewer == "anthropic" &&
                r.Model == "claude-sonnet-4-6" &&
                r.CostPerAcceptedFinding == 0.12m / 3m),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecordRun_duplicate_returns_ok_with_duplicate_flag()
    {
        var existingId = Guid.NewGuid();
        _repo.RecordRunAsync(Arg.Any<AdversarialReviewRun>(), Arg.Any<CancellationToken>())
            .Returns((existingId, IsDuplicate: true));

        var result = await CreateSut().RecordRunAsync(ValidRequest(), CancellationToken.None);

        result.Should().BeAssignableTo<IStatusCodeHttpResult>()
            .Which.StatusCode.Should().Be(200);
    }

    [Theory]
    [InlineData(5, 3, 0.12, 0.04)]
    [InlineData(5, 1, 0.10, 0.10)]
    [InlineData(0, 0, 0.05, null)]
    [InlineData(3, 0, 0.05, null)]
    public async Task RecordRun_computes_cost_per_accepted_finding_correctly(
        int raised, int accepted, double costRaw, double? expectedRaw)
    {
        var cost = (decimal)costRaw;
        decimal? expected = expectedRaw.HasValue ? (decimal)expectedRaw.Value : null;

        _repo.RecordRunAsync(Arg.Any<AdversarialReviewRun>(), Arg.Any<CancellationToken>())
            .Returns((Guid.NewGuid(), IsDuplicate: false));

        await CreateSut().RecordRunAsync(
            ValidRequest(issuesRaised: raised, issuesAccepted: accepted, costUsd: cost),
            CancellationToken.None);

        await _repo.Received(1).RecordRunAsync(
            Arg.Is<AdversarialReviewRun>(r => r.CostPerAcceptedFinding == expected),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null!)]
    public async Task RecordRun_missing_reviewer_returns_bad_request(string? reviewer)
    {
        var req = ValidRequest(reviewer: reviewer!);
        var result = await CreateSut().RecordRunAsync(req, CancellationToken.None);

        result.Should().BeAssignableTo<IStatusCodeHttpResult>()
            .Which.StatusCode.Should().Be(400);
        await _repo.DidNotReceive().RecordRunAsync(Arg.Any<AdversarialReviewRun>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null!)]
    public async Task RecordRun_missing_run_id_returns_bad_request(string? runId)
    {
        var req = ValidRequest(runId: runId!);
        var result = await CreateSut().RecordRunAsync(req, CancellationToken.None);

        result.Should().BeAssignableTo<IStatusCodeHttpResult>()
            .Which.StatusCode.Should().Be(400);
        await _repo.DidNotReceive().RecordRunAsync(Arg.Any<AdversarialReviewRun>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecordRun_issues_accepted_exceeds_raised_returns_bad_request()
    {
        var req = ValidRequest(issuesRaised: 2, issuesAccepted: 5);
        var result = await CreateSut().RecordRunAsync(req, CancellationToken.None);

        result.Should().BeAssignableTo<IStatusCodeHttpResult>()
            .Which.StatusCode.Should().Be(400);
        await _repo.DidNotReceive().RecordRunAsync(Arg.Any<AdversarialReviewRun>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecordRun_negative_cost_returns_bad_request()
    {
        var req = ValidRequest(costUsd: -0.01m);
        var result = await CreateSut().RecordRunAsync(req, CancellationToken.None);

        result.Should().BeAssignableTo<IStatusCodeHttpResult>()
            .Which.StatusCode.Should().Be(400);
        await _repo.DidNotReceive().RecordRunAsync(Arg.Any<AdversarialReviewRun>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecordRun_normalises_reviewer_to_lowercase()
    {
        _repo.RecordRunAsync(Arg.Any<AdversarialReviewRun>(), Arg.Any<CancellationToken>())
            .Returns((Guid.NewGuid(), IsDuplicate: false));

        await CreateSut().RecordRunAsync(ValidRequest(reviewer: "Anthropic"), CancellationToken.None);

        await _repo.Received(1).RecordRunAsync(
            Arg.Is<AdversarialReviewRun>(r => r.Reviewer == "anthropic"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecordRun_stamps_recorded_at_from_clock()
    {
        _repo.RecordRunAsync(Arg.Any<AdversarialReviewRun>(), Arg.Any<CancellationToken>())
            .Returns((Guid.NewGuid(), IsDuplicate: false));

        await CreateSut().RecordRunAsync(ValidRequest(), CancellationToken.None);

        await _repo.Received(1).RecordRunAsync(
            Arg.Is<AdversarialReviewRun>(r => r.RecordedAt == _clock.GetCurrentInstant()),
            Arg.Any<CancellationToken>());
    }
}
