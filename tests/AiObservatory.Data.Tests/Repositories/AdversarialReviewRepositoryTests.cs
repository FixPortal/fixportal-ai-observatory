using AiObservatory.Data;
using AiObservatory.Data.Entities;
using AiObservatory.Data.Repositories;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Xunit;

namespace AiObservatory.Data.Tests.Repositories;

// Requires TEST_DB_CONNECTION env var pointing at a real PostgreSQL instance.
public class AdversarialReviewRepositoryTests : IAsyncLifetime
{
    private string _connStr = null!;
    private AiObservatoryDbContext _ctx = null!;
    private IAdversarialReviewRepository _repo = null!;

    public async ValueTask InitializeAsync()
    {
        _connStr = Environment.GetEnvironmentVariable("TEST_DB_CONNECTION")
            ?? "Host=localhost;Database=aiobs_test;Username=postgres;Password=postgres";
        var options = new DbContextOptionsBuilder<AiObservatoryDbContext>()
            .UseNpgsql(_connStr, o => o.UseNodaTime())
            .Options;
        _ctx = new AiObservatoryDbContext(options);
        await _ctx.Database.MigrateAsync();
        _repo = new AdversarialReviewRepository(_ctx);
    }

    public async ValueTask DisposeAsync()
    {
        if (_connStr.Contains("_test", StringComparison.OrdinalIgnoreCase))
        {
            await _ctx.Database.EnsureDeletedAsync();
        }
        await _ctx.DisposeAsync();
    }

    private static AdversarialReviewRun Run(string runId, string reviewer, string role, string model) => new()
    {
        RunId = runId, Reviewer = reviewer, Role = role, Model = model,
        IssuesRaised = role == "judge" ? 0 : 3, IssuesAccepted = role == "judge" ? 0 : 2,
        CostUsd = 0.10m, ReviewDurationMs = 1000, RecordedAt = Instant.FromUtc(2026, 6, 27, 12, 0)
    };

    [Fact]
    public async Task Four_participants_sharing_one_runId_all_persist()
    {
        var ct = TestContext.Current.CancellationToken;
        var id1 = await _repo.RecordRunAsync(Run("R1", "anthropic", "reviewer", "claude-sonnet-4-6"), ct);
        var id2 = await _repo.RecordRunAsync(Run("R1", "google", "reviewer", "gemini-2.5-pro"), ct);
        var id3 = await _repo.RecordRunAsync(Run("R1", "openai", "reviewer", "gpt-5.4"), ct);
        var id4 = await _repo.RecordRunAsync(Run("R1", "anthropic", "judge", "claude-opus-4-8"), ct);

        id1.IsDuplicate.Should().BeFalse();
        id2.IsDuplicate.Should().BeFalse();
        id3.IsDuplicate.Should().BeFalse();
        id4.IsDuplicate.Should().BeFalse();
        (await _repo.GetRunsAsync(ct)).Where(r => r.RunId == "R1").Should().HaveCount(4);
    }

    [Fact]
    public async Task Re_emitting_same_runId_reviewer_role_dedups()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repo.RecordRunAsync(Run("R2", "anthropic", "reviewer", "claude-sonnet-4-6"), ct);
        var second = await _repo.RecordRunAsync(Run("R2", "anthropic", "reviewer", "claude-sonnet-4-6"), ct);
        second.IsDuplicate.Should().BeTrue();
        (await _repo.GetRunsAsync(ct)).Where(r => r.RunId == "R2").Should().HaveCount(1);
    }

    [Fact]
    public async Task DeleteAllRuns_clears_the_table()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repo.RecordRunAsync(Run("R3", "anthropic", "reviewer", "claude-sonnet-4-6"), ct);
        var deleted = await _repo.DeleteAllRunsAsync(ct);
        deleted.Should().BeGreaterThan(0);
        (await _repo.GetRunsAsync(ct)).Should().BeEmpty();
    }
}
